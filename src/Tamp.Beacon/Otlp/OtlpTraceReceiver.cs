using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;
using ExportTraceRequest = OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest;

namespace Tamp.Beacon.Otlp;

/// <summary>
/// Parses an OTLP <c>ExportTraceServiceRequest</c> and persists the spans
/// into the beacon store under a specific <see cref="Project"/>. Maps spans
/// by their owning instrumentation scope name:
/// <list type="bullet">
///   <item><c>Tamp.Build</c> → Build row</item>
///   <item><c>Tamp.Build.Targets</c> → Target row (attached to its parent Build by traceId)</item>
///   <item><c>Tamp.Build.Commands</c> → Command row (attached to its parent Target by parentSpanId)</item>
/// </list>
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

    /// <summary>ADR-0018 tag carrying the BuildConfig name on a Build span.</summary>
    public const string ConfigNameTag = "tamp.build.config.name";

    /// <summary>Reserved BuildConfig slug used when adopters don't supply <see cref="ConfigNameTag"/>.</summary>
    public const string DefaultConfigSlug = "default";

    public async Task<TraceIngestResult> IngestAsync(
        ExportTraceRequest request,
        Project project,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(project);

        var allScopes = request.ResourceSpans
            .SelectMany(r => r.ScopeSpans)
            .ToList();

        if (allScopes.Count == 0)
            return new TraceIngestResult(0, 0, 0);

        // Reject non-Tamp telemetry at ingress: every scope must start with the prefix.
        foreach (var scope in allScopes)
        {
            var name = scope.Scope?.Name ?? "";
            if (!name.StartsWith(TampSourcePrefix, StringComparison.Ordinal))
                throw new OtlpRejectionException(
                    $"this is tamp-beacon; route non-Tamp telemetry to your collector of choice (saw scope '{name}')");
        }

        // Pass 1: Build spans first so Target spans resolve their parent by traceId.
        var buildSpansByTraceId = new Dictionary<string, Build>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in allScopes.Where(s => string.Equals(s.Scope?.Name, BuildSource, StringComparison.Ordinal)))
        {
            foreach (var span in scope.Spans)
            {
                var bag = TagBag.Flatten(span.Attributes);
                var configName = TagBag.GetString(bag, ConfigNameTag) ?? DefaultConfigSlug;
                var config = await ResolveOrCreateConfigAsync(project, configName, ct).ConfigureAwait(false);
                var build = MaterializeBuild(span, project, config, bag);
                db.Builds.Add(build);
                var traceHex = HexOf(span.TraceId);
                if (!string.IsNullOrEmpty(traceHex))
                    buildSpansByTraceId[traceHex] = build;
            }
        }

        if (buildSpansByTraceId.Count > 0)
            await AssignSeqAndSaveBuildsAsync(buildSpansByTraceId.Values, ct).ConfigureAwait(false);

        // Pass 2: Target spans, attached to their owning Build by traceId.
        var targetSpansBySpanId = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in allScopes.Where(s => string.Equals(s.Scope?.Name, TargetSource, StringComparison.Ordinal)))
        {
            foreach (var span in scope.Spans)
            {
                var traceHex = HexOf(span.TraceId);
                if (!buildSpansByTraceId.TryGetValue(traceHex, out var owningBuild))
                {
                    owningBuild = await ResolveOrCreateOrphanBuildAsync(span, project, ct).ConfigureAwait(false);
                    buildSpansByTraceId[string.IsNullOrEmpty(traceHex) ? Guid.NewGuid().ToString("N") : traceHex] = owningBuild;
                }
                var target = MaterializeTarget(span, owningBuild);
                db.Targets.Add(target);
                var spanHex = HexOf(span.SpanId);
                if (!string.IsNullOrEmpty(spanHex))
                    targetSpansBySpanId[spanHex] = target;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Pass 3: Command spans, attached to their parent Target by parentSpanId.
        var commandCount = 0;
        foreach (var scope in allScopes.Where(s => string.Equals(s.Scope?.Name, CommandSource, StringComparison.Ordinal)))
        {
            foreach (var span in scope.Spans)
            {
                var parentHex = HexOf(span.ParentSpanId);
                if (string.IsNullOrEmpty(parentHex) ||
                    !targetSpansBySpanId.TryGetValue(parentHex, out var owningTarget))
                {
                    continue;
                }
                var command = MaterializeCommand(span, owningTarget);
                db.Commands.Add(command);
                commandCount++;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Pass 4: span-events emitted on the Build spans → Event rows.
        var eventCount = 0;
        foreach (var scope in allScopes.Where(s => string.Equals(s.Scope?.Name, BuildSource, StringComparison.Ordinal)))
        {
            foreach (var span in scope.Spans)
            {
                var traceHex = HexOf(span.TraceId);
                if (!buildSpansByTraceId.TryGetValue(traceHex, out var owningBuild)) continue;
                foreach (var evt in span.Events)
                {
                    var bag = TagBag.Flatten(evt.Attributes);
                    db.Events.Add(new Event
                    {
                        BuildId = owningBuild.Id,
                        Name = evt.Name,
                        AtUnixNs = (long)evt.TimeUnixNano,
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

    private async Task<Build> ResolveOrCreateOrphanBuildAsync(OtlpSpan span, Project project, CancellationToken ct)
    {
        var bag = TagBag.Flatten(span.Attributes);
        var projectName = TagBag.GetString(bag, "tamp.build.project.name") ?? project.Slug;
        var configName = TagBag.GetString(bag, ConfigNameTag) ?? DefaultConfigSlug;
        var config = await ResolveOrCreateConfigAsync(project, configName, ct).ConfigureAwait(false);
        var build = new Build
        {
            Seq = 0,
            ProjectId = project.Id,
            BuildConfigId = config.Id,
            ProjectName = projectName,
            ProjectArea = TagBag.GetString(bag, "tamp.build.project.area"),
            Organization = TagBag.GetString(bag, "tamp.build.organization") ?? project.Slug,
            StartedUnixNs = (long)span.StartTimeUnixNano,
            DurationNs = 0,
            ExitCode = 0,
            Outcome = "success",
            RawTags = "{}",
        };
        db.Builds.Add(build);
        await AssignSeqAndSaveBuildsAsync(new[] { build }, ct).ConfigureAwait(false);
        return build;
    }

    /// <summary>
    /// Look up an active BuildConfig by (project, slug). Auto-creates on first
    /// sighting — first ingest from a new pipeline doesn't 422; it lands and
    /// surfaces in the project's configs list for the admin to rename or
    /// describe later.
    /// </summary>
    private async Task<BuildConfig> ResolveOrCreateConfigAsync(Project project, string rawName, CancellationToken ct)
    {
        var slug = NormalizeConfigSlug(rawName);
        var existing = await db.BuildConfigs
            .FirstOrDefaultAsync(c => c.ProjectId == project.Id && c.Slug == slug && c.ArchivedAt == null, ct)
            .ConfigureAwait(false);
        if (existing is not null) return existing;

        var now = DateTimeOffset.UtcNow;
        var config = new BuildConfig
        {
            ProjectId = project.Id,
            Slug = slug,
            Name = rawName.Trim().Length == 0 ? slug : rawName.Trim(),
            CreatedAt = now,
        };
        db.BuildConfigs.Add(config);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return config;
    }

    private static string NormalizeConfigSlug(string raw)
    {
        // Mirror the project-slug rules: lowercase a-z 0-9 hyphen, 2-64
        // chars, no leading/trailing hyphen. Inputs that don't fit get
        // squashed: spaces + non-allowed chars → hyphens; collapse runs;
        // trim hyphens. Empty result → "default".
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return DefaultConfigSlug;
        var sb = new System.Text.StringBuilder(s.Length);
        char? last = null;
        foreach (var c in s)
        {
            var allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            char emit = allowed ? c : '-';
            if (emit == '-' && last == '-') continue;
            sb.Append(emit);
            last = emit;
        }
        var trimmed = sb.ToString().Trim('-');
        if (trimmed.Length < 2) return DefaultConfigSlug;
        if (trimmed.Length > 64) trimmed = trimmed[..64].TrimEnd('-');
        return trimmed.Length < 2 ? DefaultConfigSlug : trimmed;
    }

    private async Task AssignSeqAndSaveBuildsAsync(IEnumerable<Build> builds, CancellationToken ct)
    {
        // Each Build needs a unique monotonic seq. Postgres serializes concurrent
        // writers via the unique index on builds.seq — if two ingest requests race
        // the loser retries; v0.1.0 is single-instance so retries are rare.
        var maxSeq = await db.Builds.MaxAsync(b => (long?)b.Seq, ct).ConfigureAwait(false) ?? 0;
        long next = maxSeq + 1;
        foreach (var b in builds)
        {
            if (b.Seq == 0) b.Seq = next++;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static Build MaterializeBuild(OtlpSpan span, Project project, BuildConfig config, Dictionary<string, object?> bag)
    {
        var projectName = TagBag.GetString(bag, "tamp.build.project.name") ?? project.Slug;
        // The project + config FKs are the auth-derived sources of truth;
        // the tags are adopter-controlled and saved as denormalized hints
        // for the SPA.
        var organization = TagBag.GetString(bag, "tamp.build.organization") ?? project.Slug;
        return new Build
        {
            Seq = 0,
            ProjectId = project.Id,
            BuildConfigId = config.Id,
            ProjectName = projectName,
            ProjectArea = TagBag.GetString(bag, "tamp.build.project.area"),
            Organization = organization,
            CliVersion = TagBag.GetString(bag, "tamp.build.cli_version"),
            StartedUnixNs = (long)span.StartTimeUnixNano,
            DurationNs = TagBag.GetLong(bag, "tamp.build.duration_ns",
                fallback: ParseDuration(span)),
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

    private static Target MaterializeTarget(OtlpSpan span, Build owningBuild)
    {
        var bag = TagBag.Flatten(span.Attributes);
        return new Target
        {
            Build = owningBuild,
            BuildId = owningBuild.Id,
            Name = TagBag.GetString(bag, "tamp.target.name") ?? span.Name,
            Phase = TagBag.GetString(bag, "tamp.target.phase"),
            Status = TagBag.GetString(bag, "tamp.target.status") ?? "success",
            StartedUnixNs = (long)span.StartTimeUnixNano,
            DurationNs = TagBag.GetLong(bag, "tamp.target.duration_ns",
                fallback: ParseDuration(span)),
            CpuTimeMs = TagBag.GetDouble(bag, "tamp.target.cpu_time_ms"),
            GcAllocatedBytes = TagBag.GetLong(bag, "tamp.target.gc_allocated_bytes"),
            GcGen0 = TagBag.GetInt(bag, "tamp.target.gc.gen0.collections"),
            GcGen1 = TagBag.GetInt(bag, "tamp.target.gc.gen1.collections"),
            GcGen2 = TagBag.GetInt(bag, "tamp.target.gc.gen2.collections"),
            CommandsCount = TagBag.GetInt(bag, "tamp.target.commands.count"),
            RawTags = TagBag.ToJson(bag),
        };
    }

    private static Command MaterializeCommand(OtlpSpan span, Target owningTarget)
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
                fallback: ParseDuration(span)),
            CpuTotalMs = TagBag.GetDouble(bag, "tamp.cmd.child.cpu_time.total_ms"),
            PeakMemoryBytes = TagBag.GetLong(bag, "tamp.cmd.child.peak_working_set_bytes"),
            StdoutBytes = TagBag.GetLong(bag, "tamp.cmd.stdout_bytes"),
            StderrBytes = TagBag.GetLong(bag, "tamp.cmd.stderr_bytes"),
            RawTags = TagBag.ToJson(bag),
        };
    }

    private static long ParseDuration(OtlpSpan span) =>
        span.EndTimeUnixNano > span.StartTimeUnixNano
            ? (long)(span.EndTimeUnixNano - span.StartTimeUnixNano)
            : 0;

    private static string HexOf(Google.Protobuf.ByteString bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        return Convert.ToHexStringLower(bytes.Span);
    }
}

/// <summary>Counts of rows persisted from a single ingest call.</summary>
public sealed record TraceIngestResult(int BuildsIngested, int TargetsIngested, int CommandsIngested);
