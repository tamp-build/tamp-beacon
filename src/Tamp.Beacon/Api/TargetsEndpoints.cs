using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Sdk;

namespace Tamp.Beacon.Api;

public static class TargetsEndpoints
{
    public static IEndpointRouteBuilder MapTargets(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/targets/slowest", GetSlowestAsync);
        app.MapGet("/api/targets/flakiest", GetFlakiestAsync);
        return app;
    }

    private static async Task<IResult> GetSlowestAsync(
        BeaconDbContext db,
        [FromQuery] string? project,
        [FromQuery(Name = "since_unix_ns")] long? sinceUnixNs,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 20, 1, 200);
        var query =
            from t in db.Targets.AsNoTracking()
            join b in db.Builds.AsNoTracking() on t.BuildId equals b.Id
            where (project == null || b.ProjectName == project)
               && (sinceUnixNs == null || t.StartedUnixNs >= sinceUnixNs)
            select new { t, b };

        // Group + aggregate in memory: EF Core's GROUP BY translation can't always emit
        // PERCENTILE_DISC against SQLite (it lacks the function), so we project the raw
        // rows then post-process. v0.1.0 row counts make this fine.
        var rows = await query
            .Select(x => new { x.t.Name, x.b.ProjectName, x.t.DurationNs })
            .ToListAsync(ct);

        var grouped = rows
            .GroupBy(x => new { x.Name, x.ProjectName })
            .Select(g =>
            {
                var samples = g.Select(x => x.DurationNs).OrderBy(x => x).ToList();
                return new TargetStat
                {
                    Name = g.Key.Name,
                    ProjectName = g.Key.ProjectName,
                    AvgDurationNs = samples.Average(),
                    P95DurationNs = Percentile(samples, 0.95),
                    Samples = samples.Count,
                };
            })
            .OrderByDescending(s => s.AvgDurationNs)
            .Take(take)
            .ToList();

        return Results.Ok(new TargetStatList { Targets = grouped });
    }

    private static async Task<IResult> GetFlakiestAsync(
        BeaconDbContext db,
        [FromQuery] string? project,
        [FromQuery(Name = "since_unix_ns")] long? sinceUnixNs,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 20, 1, 200);
        var query =
            from t in db.Targets.AsNoTracking()
            join b in db.Builds.AsNoTracking() on t.BuildId equals b.Id
            where (project == null || b.ProjectName == project)
               && (sinceUnixNs == null || t.StartedUnixNs >= sinceUnixNs)
            select new { t, b };

        var rows = await query
            .Select(x => new { x.t.Name, x.b.ProjectName, x.t.Status })
            .ToListAsync(ct);

        var grouped = rows
            .GroupBy(x => new { x.Name, x.ProjectName })
            .Select(g =>
            {
                var total = g.Count();
                var failures = g.Count(x => string.Equals(x.Status, "failure", StringComparison.Ordinal));
                return new FlakyTarget
                {
                    Name = g.Key.Name,
                    ProjectName = g.Key.ProjectName,
                    FailRate = total == 0 ? 0 : (double)failures / total,
                    Samples = total,
                };
            })
            // Only report tuples with at least one failure AND a meaningful sample count.
            .Where(s => s.FailRate > 0 && s.Samples >= 3)
            .OrderByDescending(s => s.FailRate)
            .ThenByDescending(s => s.Samples)
            .Take(take)
            .ToList();

        return Results.Ok(new FlakyTargetList { Targets = grouped });
    }

    private static double Percentile(System.Collections.Generic.List<long> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return 0;
        if (sortedAsc.Count == 1) return sortedAsc[0];
        var rank = p * (sortedAsc.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sortedAsc[lo];
        var weight = rank - lo;
        return sortedAsc[lo] * (1 - weight) + sortedAsc[hi] * weight;
    }
}
