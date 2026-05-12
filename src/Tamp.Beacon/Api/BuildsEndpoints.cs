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

public static class BuildsEndpoints
{
    public static IEndpointRouteBuilder MapBuilds(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/builds", ListBuildsAsync);
        app.MapGet("/api/builds/{id:long}", GetBuildAsync);
        return app;
    }

    private static async Task<IResult> ListBuildsAsync(
        BeaconDbContext db,
        [FromQuery] string? project,
        [FromQuery] string? area,
        [FromQuery(Name = "since_seq")] long? sinceSeq,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        var query = db.Builds.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(project))
            query = query.Where(b => b.ProjectName == project);
        if (!string.IsNullOrEmpty(area))
            query = query.Where(b => b.ProjectArea == area);
        if (sinceSeq is { } seq)
            query = query.Where(b => b.Seq > seq);

        var take = Math.Clamp(limit ?? 50, 1, 500);

        var rows = await query
            .OrderBy(b => b.Seq)
            .Take(take)
            .Select(b => new BuildSummary
            {
                Id = b.Id,
                Seq = b.Seq,
                ProjectName = b.ProjectName,
                ProjectArea = b.ProjectArea,
                CliVersion = b.CliVersion,
                StartedUnixNs = b.StartedUnixNs,
                DurationNs = b.DurationNs,
                ExitCode = b.ExitCode,
                Outcome = b.Outcome,
                TargetsTotal = b.TargetsTotal,
                TargetsFailed = b.TargetsFailed,
                CommandsTotal = b.CommandsTotal,
                FailureTarget = b.FailureTarget,
                HostOs = b.HostOs,
                HostArch = b.HostArch,
                CiVendor = b.CiVendor,
                PeakMemoryBytes = b.PeakMemoryBytes,
            })
            .ToListAsync(ct);

        var nextSeq = rows.Count == 0 ? (sinceSeq ?? 0) : rows[^1].Seq;
        return Results.Ok(new BuildList { Builds = rows, NextSeq = nextSeq });
    }

    private static async Task<IResult> GetBuildAsync(
        BeaconDbContext db,
        long id,
        CancellationToken ct)
    {
        var build = await db.Builds.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (build is null) return Results.NotFound();

        var targets = await db.Targets.AsNoTracking()
            .Where(t => t.BuildId == id)
            .OrderBy(t => t.StartedUnixNs)
            .ToListAsync(ct);
        var targetIds = targets.Select(t => t.Id).ToList();

        var commands = await db.Commands.AsNoTracking()
            .Where(c => targetIds.Contains(c.TargetId))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        var events = await db.Events.AsNoTracking()
            .Where(e => e.BuildId == id)
            .OrderBy(e => e.AtUnixNs)
            .ToListAsync(ct);

        return Results.Ok(new BuildDetail
        {
            Build = new BuildSummary
            {
                Id = build.Id,
                Seq = build.Seq,
                ProjectName = build.ProjectName,
                ProjectArea = build.ProjectArea,
                CliVersion = build.CliVersion,
                StartedUnixNs = build.StartedUnixNs,
                DurationNs = build.DurationNs,
                ExitCode = build.ExitCode,
                Outcome = build.Outcome,
                TargetsTotal = build.TargetsTotal,
                TargetsFailed = build.TargetsFailed,
                CommandsTotal = build.CommandsTotal,
                FailureTarget = build.FailureTarget,
                HostOs = build.HostOs,
                HostArch = build.HostArch,
                CiVendor = build.CiVendor,
                PeakMemoryBytes = build.PeakMemoryBytes,
            },
            Targets = targets.Select(t => new TargetSummary
            {
                Id = t.Id,
                BuildId = t.BuildId,
                Name = t.Name,
                Phase = t.Phase,
                Status = t.Status,
                StartedUnixNs = t.StartedUnixNs,
                DurationNs = t.DurationNs,
                CpuTimeMs = t.CpuTimeMs,
                GcAllocatedBytes = t.GcAllocatedBytes,
                GcGen0 = t.GcGen0,
                GcGen1 = t.GcGen1,
                GcGen2 = t.GcGen2,
                CommandsCount = t.CommandsCount,
            }).ToList(),
            Commands = commands.Select(c => new CommandSummary
            {
                Id = c.Id,
                TargetId = c.TargetId,
                Executable = c.Executable,
                ArgsCount = c.ArgsCount,
                ExitCode = c.ExitCode,
                DurationNs = c.DurationNs,
                CpuTotalMs = c.CpuTotalMs,
                PeakMemoryBytes = c.PeakMemoryBytes,
                StdoutBytes = c.StdoutBytes,
                StderrBytes = c.StderrBytes,
            }).ToList(),
            Events = events.Select(e => new EventSummary
            {
                Id = e.Id,
                BuildId = e.BuildId,
                TargetId = e.TargetId,
                CommandId = e.CommandId,
                Name = e.Name,
                AtUnixNs = e.AtUnixNs,
            }).ToList(),
        });
    }
}
