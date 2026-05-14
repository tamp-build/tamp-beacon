using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Api;

/// <summary>
/// Aggregated target stats for a single project — the SPA dashboard uses
/// these to surface "slowest" and "flakiest" target rosters. Both endpoints
/// are RBAC-gated to project members (non-members get 404).
/// </summary>
public static class TargetsEndpoints
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 200;
    public const int DefaultSamplesMin = 3;

    public static IEndpointRouteBuilder MapTargets(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{slug}/targets/slowest", SlowestAsync).RequireAuthorization();
        app.MapGet("/api/projects/{slug}/targets/flakiest", FlakiestAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> SlowestAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        int? limit,
        long? since_unix_ns,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var sinceNs = since_unix_ns ?? 0;
        var clamp = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        // Average + sample count per (target name) within the project. Postgres
        // PERCENTILE_CONT exposed as EF.Functions in Npgsql 8+; we approximate
        // p95 by ordering targets by duration_ns and grabbing the 0.95 row in
        // C# space — at the v0.1 sample volumes this is cheap and exact.
        var rows = await db.Targets.AsNoTracking()
            .Where(t => t.Build.ProjectId == gate.Project!.Id &&
                        t.StartedUnixNs >= sinceNs)
            .Select(t => new { t.Name, t.DurationNs })
            .ToListAsync(cancel).ConfigureAwait(false);

        var grouped = rows
            .GroupBy(t => t.Name)
            .Select(g => new TargetStat
            {
                Name = g.Key,
                AvgDurationNs = g.Average(x => (double)x.DurationNs),
                P95DurationNs = Percentile(g.Select(x => x.DurationNs).ToList(), 0.95),
                Samples = g.Count(),
            })
            .OrderByDescending(s => s.AvgDurationNs)
            .Take(clamp)
            .ToList();

        return Results.Ok(new TargetStatList { Targets = grouped });
    }

    private static async Task<IResult> FlakiestAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        int? limit,
        int? samples_min,
        long? since_unix_ns,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var sinceNs = since_unix_ns ?? 0;
        var clamp = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var minSamples = Math.Max(1, samples_min ?? DefaultSamplesMin);

        var rows = await db.Targets.AsNoTracking()
            .Where(t => t.Build.ProjectId == gate.Project!.Id &&
                        t.StartedUnixNs >= sinceNs)
            .Select(t => new { t.Name, t.Status })
            .ToListAsync(cancel).ConfigureAwait(false);

        var grouped = rows
            .GroupBy(t => t.Name)
            .Where(g => g.Count() >= minSamples)
            .Select(g =>
            {
                var failed = g.Count(x => x.Status != "success" && x.Status != "skipped");
                return new FlakyTarget
                {
                    Name = g.Key,
                    FailRate = (double)failed / g.Count(),
                    Samples = g.Count(),
                };
            })
            .Where(s => s.FailRate > 0)
            .OrderByDescending(s => s.FailRate)
            .ThenByDescending(s => s.Samples)
            .Take(clamp)
            .ToList();

        return Results.Ok(new FlakyTargetList { Targets = grouped });
    }

    /// <summary>
    /// Linear-interpolation percentile on a sorted long sequence. Cheap and
    /// exact at v0.1 sample volumes; if a project ever exceeds 10⁵ targets per
    /// hour this becomes a hot path and we'll push it into Postgres via
    /// PERCENTILE_CONT.
    /// </summary>
    private static double Percentile(System.Collections.Generic.List<long> samples, double p)
    {
        if (samples.Count == 0) return 0;
        samples.Sort();
        if (samples.Count == 1) return samples[0];
        var rank = p * (samples.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return samples[lo];
        return samples[lo] + (rank - lo) * (samples[hi] - samples[lo]);
    }

    public sealed record TargetStat
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("avg_duration_ns")] public double AvgDurationNs { get; init; }
        [JsonPropertyName("p95_duration_ns")] public double P95DurationNs { get; init; }
        [JsonPropertyName("samples")] public int Samples { get; init; }
    }

    public sealed record TargetStatList
    {
        [JsonPropertyName("targets")] public System.Collections.Generic.IReadOnlyList<TargetStat> Targets { get; init; }
            = System.Array.Empty<TargetStat>();
    }

    public sealed record FlakyTarget
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("fail_rate")] public double FailRate { get; init; }
        [JsonPropertyName("samples")] public int Samples { get; init; }
    }

    public sealed record FlakyTargetList
    {
        [JsonPropertyName("targets")] public System.Collections.Generic.IReadOnlyList<FlakyTarget> Targets { get; init; }
            = System.Array.Empty<FlakyTarget>();
    }
}
