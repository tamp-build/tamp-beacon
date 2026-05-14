using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Contracts;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Api;

/// <summary>
/// Read-side surface for builds — cross-project list (RBAC-filtered),
/// per-project list, and per-build detail. The list endpoints support
/// <c>since_seq</c> delta polling: pass the last <see cref="BuildSummary.Seq"/>
/// you saw and the response carries only builds with a higher seq.
/// </summary>
public static class BuildsEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    public static IEndpointRouteBuilder MapBuilds(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/builds", ListAllAsync).RequireAuthorization();
        app.MapGet("/api/builds/{id:long}", DetailAsync).RequireAuthorization();
        app.MapGet("/api/projects/{slug}/builds", ProjectListAsync).RequireAuthorization();
        app.MapGet("/api/projects/{slug}/configs/{configSlug}/builds", ConfigListAsync).RequireAuthorization();
        return app;
    }

    // ─── GET /api/builds ─────────────────────────────────────────────────

    private static async Task<IResult> ListAllAsync(
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        long? since_seq,
        string? project,
        string? outcome,
        int? limit,
        CancellationToken cancel)
    {
        if (ProjectAuthorization.CurrentUserId(ctx) is null) return Results.Unauthorized();

        var visibleIds = await auth.VisibleProjectIdsAsync(ctx, cancel).ConfigureAwait(false);
        if (visibleIds.Length == 0)
            return Results.Ok(new BuildListResponse { Builds = System.Array.Empty<BuildSummary>(), NextSeq = since_seq ?? 0 });

        var query = db.Builds.AsNoTracking().Where(b => visibleIds.Contains(b.ProjectId));

        if (since_seq is long sinceSeq) query = query.Where(b => b.Seq > sinceSeq);
        if (!string.IsNullOrWhiteSpace(project))
        {
            var slug = project.Trim().ToLowerInvariant();
            query = query.Where(b => b.Project.Slug == slug);
        }
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            var o = outcome.Trim().ToLowerInvariant();
            if (o is not ("success" or "failure"))
                return Results.BadRequest(new { error = "outcome must be 'success' or 'failure'" });
            query = query.Where(b => b.Outcome == o);
        }

        var clamp = ClampLimit(limit);
        var rows = await query
            .OrderBy(b => b.Seq)
            .Take(clamp)
            .Select(b => new BuildSummary
            {
                Id = b.Id,
                Seq = b.Seq,
                ProjectSlug = b.Project.Slug,
                Organization = b.Organization,
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
            .ToListAsync(cancel).ConfigureAwait(false);

        var nextSeq = rows.Count == 0
            ? (since_seq ?? 0)
            : rows[^1].Seq;

        return Results.Ok(new BuildListResponse { Builds = rows, NextSeq = nextSeq });
    }

    // ─── GET /api/projects/{slug}/builds ─────────────────────────────────

    private static async Task<IResult> ProjectListAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        long? since_seq,
        string? outcome,
        int? limit,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var query = db.Builds.AsNoTracking().Where(b => b.ProjectId == gate.Project!.Id);

        if (since_seq is long sinceSeq) query = query.Where(b => b.Seq > sinceSeq);
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            var o = outcome.Trim().ToLowerInvariant();
            if (o is not ("success" or "failure"))
                return Results.BadRequest(new { error = "outcome must be 'success' or 'failure'" });
            query = query.Where(b => b.Outcome == o);
        }

        var clamp = ClampLimit(limit);
        var rows = await query
            .OrderBy(b => b.Seq)
            .Take(clamp)
            .Select(b => new BuildSummary
            {
                Id = b.Id,
                Seq = b.Seq,
                ProjectSlug = b.Project.Slug,
                Organization = b.Organization,
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
            .ToListAsync(cancel).ConfigureAwait(false);

        var nextSeq = rows.Count == 0
            ? (since_seq ?? 0)
            : rows[^1].Seq;
        return Results.Ok(new BuildListResponse { Builds = rows, NextSeq = nextSeq });
    }

    // ─── GET /api/projects/{slug}/configs/{configSlug}/builds ────────────

    private static async Task<IResult> ConfigListAsync(
        string slug,
        string configSlug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        long? since_seq,
        string? outcome,
        int? limit,
        CancellationToken cancel)
    {
        var (gate, config) = await BuildConfigsEndpoints.GateAsync(
            ctx, slug, configSlug, ProjectRole.Viewer, auth, db, cancel).ConfigureAwait(false);
        if (gate is not null) return gate;

        var query = db.Builds.AsNoTracking().Where(b => b.BuildConfigId == config!.Id);

        if (since_seq is long sinceSeq) query = query.Where(b => b.Seq > sinceSeq);
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            var o = outcome.Trim().ToLowerInvariant();
            if (o is not ("success" or "failure"))
                return Results.BadRequest(new { error = "outcome must be 'success' or 'failure'" });
            query = query.Where(b => b.Outcome == o);
        }

        var clamp = ClampLimit(limit);
        var rows = await query
            .OrderBy(b => b.Seq)
            .Take(clamp)
            .Select(b => new BuildSummary
            {
                Id = b.Id,
                Seq = b.Seq,
                ProjectSlug = b.Project.Slug,
                Organization = b.Organization,
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
            .ToListAsync(cancel).ConfigureAwait(false);

        var nextSeq = rows.Count == 0 ? (since_seq ?? 0) : rows[^1].Seq;
        return Results.Ok(new BuildListResponse { Builds = rows, NextSeq = nextSeq });
    }

    // ─── GET /api/builds/{id} ────────────────────────────────────────────

    private static async Task<IResult> DetailAsync(
        long id,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        if (ProjectAuthorization.CurrentUserId(ctx) is null) return Results.Unauthorized();

        var build = await db.Builds.AsNoTracking()
            .Include(b => b.Project)
            .FirstOrDefaultAsync(b => b.Id == id, cancel).ConfigureAwait(false);
        if (build is null) return Results.NotFound(new { error = "build not found" });

        // RBAC — caller must be able to see this project. Re-use the
        // single-project gate so non-members get 404, not 403.
        var gate = await auth.RequireAsync(ctx, build.Project.Slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var targets = await db.Targets.AsNoTracking()
            .Where(t => t.BuildId == build.Id)
            .OrderBy(t => t.StartedUnixNs)
            .Select(t => new TargetSummary
            {
                Id = t.Id, BuildId = t.BuildId, Name = t.Name, Phase = t.Phase,
                Status = t.Status, StartedUnixNs = t.StartedUnixNs, DurationNs = t.DurationNs,
                CpuTimeMs = t.CpuTimeMs, GcAllocatedBytes = t.GcAllocatedBytes,
                GcGen0 = t.GcGen0, GcGen1 = t.GcGen1, GcGen2 = t.GcGen2,
                CommandsCount = t.CommandsCount,
            })
            .ToListAsync(cancel).ConfigureAwait(false);

        var targetIds = targets.Select(t => t.Id).ToList();
        var commands = await db.Commands.AsNoTracking()
            .Where(c => targetIds.Contains(c.TargetId))
            .Select(c => new CommandSummary
            {
                Id = c.Id, TargetId = c.TargetId, Executable = c.Executable,
                ArgsCount = c.ArgsCount, ExitCode = c.ExitCode, DurationNs = c.DurationNs,
                CpuTotalMs = c.CpuTotalMs, PeakMemoryBytes = c.PeakMemoryBytes,
                StdoutBytes = c.StdoutBytes, StderrBytes = c.StderrBytes,
            })
            .ToListAsync(cancel).ConfigureAwait(false);

        var events = await db.Events.AsNoTracking()
            .Where(e => e.BuildId == build.Id)
            .OrderBy(e => e.AtUnixNs)
            .Select(e => new EventSummary
            {
                Id = e.Id, BuildId = e.BuildId, TargetId = e.TargetId,
                CommandId = e.CommandId, Name = e.Name, AtUnixNs = e.AtUnixNs,
            })
            .ToListAsync(cancel).ConfigureAwait(false);

        return Results.Ok(new BuildDetail
        {
            Build = SummarizeFromEntity(build),
            Targets = targets,
            Commands = commands,
            Events = events,
        });
    }

    private static int ClampLimit(int? limit) =>
        limit is null ? DefaultLimit : System.Math.Clamp(limit.Value, 1, MaxLimit);

    /// <summary>
    /// Materialize the in-memory <see cref="BuildSummary"/> for the detail
    /// endpoint, which already has the <c>Project</c> nav loaded via Include.
    /// </summary>
    private static BuildSummary SummarizeFromEntity(Build b) => new()
    {
        Id = b.Id,
        Seq = b.Seq,
        ProjectSlug = b.Project.Slug,
        Organization = b.Organization,
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
    };

    public sealed record BuildListResponse
    {
        [JsonPropertyName("builds")] public System.Collections.Generic.IReadOnlyList<BuildSummary> Builds { get; init; }
            = System.Array.Empty<BuildSummary>();
        [JsonPropertyName("next_seq")] public long NextSeq { get; init; }
    }
}
