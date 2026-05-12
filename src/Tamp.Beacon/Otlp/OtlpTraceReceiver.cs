using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Otlp;

/// <summary>
/// Parses an OTLP/HTTP-JSON ExportTraceServiceRequest envelope and persists
/// the spans into the beacon's SQLite store. Maps spans by their owning
/// instrumentation scope name:
///
/// <list type="bullet">
///   <item><c>Tamp.Build</c> → Build row</item>
///   <item><c>Tamp.Build.Targets</c> → Target row (attached to the parent Build by traceId)</item>
///   <item><c>Tamp.Build.Commands</c> → Command row (attached to the parent Target by parentSpanId)</item>
/// </list>
///
/// Span events on the Build span (<c>tamp.build.summary</c> etc.) flow through to the
/// <c>events</c> table. The whole tag dict is preserved in <c>raw_tags</c> so unknown tags
/// remain queryable from the JSON column without a schema migration.
/// </summary>
public sealed class OtlpTraceReceiver(BeaconDbContext db)
{
    /// <summary>Source-name prefix the receiver accepts. Other prefixes are rejected with 422.</summary>
    public const string TampSourcePrefix = "Tamp.Build";

    public const string BuildSource = "Tamp.Build";
    public const string TargetSource = "Tamp.Build.Targets";
    public const string CommandSource = "Tamp.Build.Commands";

    public async Task<TraceIngestResult> IngestAsync(
        ExportTraceServiceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var allScopes = request.ResourceSpans
            .SelectMany(r => r.ScopeSpans)
            .ToList();

        if (allScopes.Count == 0)
            return new TraceIngestResult(0, 0, 0);

        // Reject non-Tamp telemetry at ingress: every scope must start with the prefix.
        // We accept that this is strict; misrouted Tamp.Build sibling sources (e.g. Tamp.Otel)
        // are anchored under the same prefix so the strict check still lets future
        // adjacent scopes through.
        foreach (var scope in allScopes)
        {
            var name = scope.Scope?.Name ?? "";
            if (!name.StartsWith(TampSourcePrefix, StringComparison.Ordinal))
                throw new OtlpRejectionException(
                    $"this is tamp-beacon; route non-Tamp telemetry to your collector of choice (saw scope '{name}')");
        }

        // Pass 1: ingest Build spans first so Target spans can resolve their parent Build by traceId.
        var buildSpansByTraceId = new Dictionary<string, Build>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in allScopes.Where(s => string.Equals(s.Scope?.Name, BuildSource, StringComparison.Ordinal)))
        {
            foreach (var span in scope.Spans)
            {
                var build = MaterializeBuild(span);
                db.Builds.Add(build);
                if (!string.IsNullOrEmpty(span.TraceId))
                    buildSpansByTraceId[span.TraceId] = build;
            }
        }

        // Persist Builds first so their IDs are populated for the subsequent target/command FKs.
        if (buildSpansByTraceId.Count > 0)
            await AssignSeqAndSaveBuildsAsync(buildSpansByTraceId.Values, ct).ConfigureAwait(false);

        // Pass 2: ingest Target spans, attached to the Build with the matching traceId.
        var targetSpansBySpanId = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in allScopes.Where(s => string.Equals(s.Scope?.Name, TargetSource, StringComparison.Ordinal)))
        {
            foreach (var span in scope.Spans)
            {
                if (!buildSpansByTraceId.TryGetValue(span.TraceId ?? "", out var owningBuild))
                {
                    // Orphan target span — no Build was ingested in this batch with the same trace.
                    // We still record it under a synthetic Build to preserve data; tests cover this path.
                    owningBuild = await ResolveOrCreateOrphanBuildAsync(span, ct).ConfigureAwait(false);
                    buildSpansByTraceId[span.TraceId ?? Guid.NewGuid().ToString("N")] = owningBuild;
                }

                var target = MaterializeTarget(span, owningBuild);
                db.Targets.Add(target);
                if (!string.IsNullOrEmpty(span.SpanId))
                    targetSpansBySpanId[span.SpanId] = target;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Pass 3: ingest Command spans, attached to their parent Target by parentSpanId.
        var commandCount = 0;
        foreach (var scope in allScopes.Where(s => string.Equals(s.Scope?.Name, CommandSource, StringComparison.Ordinal)))
        {
            foreach (var span in scope.Spans)
            {
                if (string.IsNullOrEmpty(span.ParentSpanId) ||
                    !targetSpansBySpanId.TryGetValue(span.ParentSpanId, out var owningTarget))
                {
                    // Orphan command — no Target span in this batch matches the parentSpanId.
                    // Pre-existing Target spans from earlier requests aren't looked up here
                    // (v0.1.0 simplification — commands without their target in the same OTLP
                    // batch are a corner case for streaming exporters; revisit if it matters).
                    continue;
                }
                var command = MaterializeCommand(span, owningTarget);
                db.Commands.Add(command);
                commandCount++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Pass 4: ingest span-events emitted on the Build spans as Event rows.
        var eventCount = 0;
        foreach (var scope in allScopes.Where(s => string.Equals(s.Scope?.Name, BuildSource, StringComparison.Ordinal)))
        {
            foreach (var span in scope.Spans)
            {
                if (!buildSpansByTraceId.TryGetValue(span.TraceId ?? "", out var owningBuild)) continue;
                foreach (var evt in span.Events)
                {
                    var bag = TagBag.Flatten(evt.Attributes);
                    db.Events.Add(new Event
                    {
                        BuildId = owningBuild.Id,
                        Name = evt.Name,
                        AtUnixNs = long.TryParse(evt.TimeUnixNano, out var t) ? t : 0,
                        RawTags = TagBag.ToJson(bag),
                    });
                    eventCount++;
                }
            }
        }
        if (eventCount > 0) await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new TraceIngestResult(
            BuildsIngested: buildSpansByTraceId.Count,
            TargetsIngested: targetSpansBySpanId.Count,
            CommandsIngested: commandCount);
    }

    private async Task<Build> ResolveOrCreateOrphanBuildAsync(Span span, CancellationToken ct)
    {
        // Try to find an existing Build span in the DB that owns this traceId.
        // We use the SpanId of the Build span as the foreign key surrogate via parentSpanId chain;
        // for v0.1.0 we simply create a synthetic Build with attributes lifted from the orphan target.
        var bag = TagBag.Flatten(span.Attributes);
        var build = new Build
        {
            Seq = 0, // assigned in SaveChanges path
            ProjectName = TagBag.GetString(bag, "tamp.build.project.name") ?? "unknown",
            ProjectArea = TagBag.GetString(bag, "tamp.build.project.area"),
            StartedUnixNs = long.TryParse(span.StartTimeUnixNano, out var st) ? st : 0,
            DurationNs = 0,
            ExitCode = 0,
            Outcome = "success",
            RawTags = "{}",
        };
        db.Builds.Add(build);
        await AssignSeqAndSaveBuildsAsync(new[] { build }, ct).ConfigureAwait(false);
        return build;
    }

    private async Task AssignSeqAndSaveBuildsAsync(IEnumerable<Build> builds, CancellationToken ct)
    {
        // Each Build needs a unique monotonic seq. We pull the current max in one query
        // and assign incrementally. Concurrent writers from multiple requests would race;
        // SQLite's serialized-write model is sufficient for the trusted-network v0.1.0 case.
        var maxSeq = await db.Builds.MaxAsync(b => (long?)b.Seq, ct).ConfigureAwait(false) ?? 0;
        long next = maxSeq + 1;
        foreach (var b in builds)
        {
            if (b.Seq == 0) b.Seq = next++;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static Build MaterializeBuild(Span span)
    {
        var bag = TagBag.Flatten(span.Attributes);
        return new Build
        {
            Seq = 0, // assigned at save time
            ProjectName = TagBag.GetString(bag, "tamp.build.project.name") ?? "unknown",
            ProjectArea = TagBag.GetString(bag, "tamp.build.project.area"),
            CliVersion = TagBag.GetString(bag, "tamp.build.cli_version"),
            StartedUnixNs = long.TryParse(span.StartTimeUnixNano, out var st) ? st : 0,
            DurationNs = TagBag.GetLong(bag, "tamp.build.duration_ns",
                fallback: ParseDurationFromTimes(span.StartTimeUnixNano, span.EndTimeUnixNano)),
            ExitCode = TagBag.GetInt(bag, "tamp.build.exit_code"),
            Outcome = TagBag.GetString(bag, "outcome") ?? "success",
            TargetsTotal = TagBag.GetInt(bag, "tamp.build.targets.total"),
            TargetsFailed = TagBag.GetInt(bag, "tamp.build.targets.failed"),
            CommandsTotal = TagBag.GetInt(bag, "tamp.build.commands.total"),
            FailureTarget = TagBag.GetString(bag, "tamp.build.failure.target"),
            HostOs = TagBag.GetString(bag, "tamp.host.os"),
            HostArch = TagBag.GetString(bag, "tamp.host.arch"),
            CiVendor = TagBag.GetString(bag, "tamp.ci.vendor"),
            PeakMemoryBytes = TagBag.GetLong(bag, "tamp.build.peak_working_set_bytes"),
            RawTags = TagBag.ToJson(bag),
        };
    }

    private static Target MaterializeTarget(Span span, Build owningBuild)
    {
        var bag = TagBag.Flatten(span.Attributes);
        return new Target
        {
            Build = owningBuild,
            BuildId = owningBuild.Id,
            Name = TagBag.GetString(bag, "tamp.target.name") ?? span.Name,
            Phase = TagBag.GetString(bag, "tamp.target.phase"),
            Status = TagBag.GetString(bag, "tamp.target.status") ?? "success",
            StartedUnixNs = long.TryParse(span.StartTimeUnixNano, out var st) ? st : 0,
            DurationNs = TagBag.GetLong(bag, "tamp.target.duration_ns",
                fallback: ParseDurationFromTimes(span.StartTimeUnixNano, span.EndTimeUnixNano)),
            CpuTimeMs = TagBag.GetDouble(bag, "tamp.target.cpu_time_ms"),
            GcAllocatedBytes = TagBag.GetLong(bag, "tamp.target.gc_allocated_bytes"),
            GcGen0 = TagBag.GetInt(bag, "tamp.target.gc.gen0.collections"),
            GcGen1 = TagBag.GetInt(bag, "tamp.target.gc.gen1.collections"),
            GcGen2 = TagBag.GetInt(bag, "tamp.target.gc.gen2.collections"),
            CommandsCount = TagBag.GetInt(bag, "tamp.target.commands.count"),
            RawTags = TagBag.ToJson(bag),
        };
    }

    private static Command MaterializeCommand(Span span, Target owningTarget)
    {
        var bag = TagBag.Flatten(span.Attributes);
        return new Command
        {
            Target = owningTarget,
            TargetId = owningTarget.Id,
            Executable = TagBag.GetString(bag, "tamp.cmd.executable") ?? span.Name,
            ArgsCount = TagBag.GetInt(bag, "tamp.cmd.args.count"),
            ExitCode = TagBag.GetInt(bag, "tamp.cmd.exit_code"),
            DurationNs = TagBag.GetLong(bag, "tamp.cmd.duration_ns",
                fallback: ParseDurationFromTimes(span.StartTimeUnixNano, span.EndTimeUnixNano)),
            CpuTotalMs = TagBag.GetDouble(bag, "tamp.cmd.child.cpu_time.total_ms"),
            PeakMemoryBytes = TagBag.GetLong(bag, "tamp.cmd.child.peak_working_set_bytes"),
            StdoutBytes = TagBag.GetLong(bag, "tamp.cmd.stdout_bytes"),
            StderrBytes = TagBag.GetLong(bag, "tamp.cmd.stderr_bytes"),
            RawTags = TagBag.ToJson(bag),
        };
    }

    private static long ParseDurationFromTimes(string start, string end)
    {
        if (!long.TryParse(start, out var s)) return 0;
        if (!long.TryParse(end, out var e)) return 0;
        return e > s ? e - s : 0;
    }
}

/// <summary>Counts of rows persisted from a single ingest call.</summary>
/// <param name="BuildsIngested">Build spans persisted.</param>
/// <param name="TargetsIngested">Target spans persisted.</param>
/// <param name="CommandsIngested">Command spans persisted.</param>
public sealed record TraceIngestResult(int BuildsIngested, int TargetsIngested, int CommandsIngested);
