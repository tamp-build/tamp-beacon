using System;
using System.Collections.Generic;
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
/// Build-config CRUD + aggregate stats. Builds attach to configs via the
/// receiver's <c>tamp.build.config.name</c> tag (auto-created on first
/// ingest); admins can rename, describe, archive. Configs are scoped to a
/// project — the slice-3 RBAC gate runs on the parent project for every
/// endpoint here.
/// </summary>
public static class BuildConfigsEndpoints
{
    public static IEndpointRouteBuilder MapBuildConfigs(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{slug}/configs").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{configSlug}", GetAsync);
        group.MapPatch("/{configSlug}", UpdateAsync);
        group.MapDelete("/{configSlug}", ArchiveAsync);
        return app;
    }

    // ─── GET /api/projects/{slug}/configs ─────────────────────────────────

    private static async Task<IResult> ListAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var configs = await db.BuildConfigs.AsNoTracking()
            .Where(c => c.ProjectId == gate.Project!.Id && c.ArchivedAt == null)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancel).ConfigureAwait(false);
        var configIds = configs.Select(c => c.Id).ToArray();
        if (configIds.Length == 0) return Results.Ok(new { configs = System.Array.Empty<ConfigSummary>() });

        var stats = await db.Builds.AsNoTracking()
            .Where(b => configIds.Contains(b.BuildConfigId))
            .GroupBy(b => b.BuildConfigId)
            .Select(g => new
            {
                ConfigId = g.Key,
                TotalBuilds = g.Count(),
                LastSeq = g.Max(b => b.Seq),
            })
            .ToListAsync(cancel).ConfigureAwait(false);

        // builds.seq is globally unique (slice 1 ix_builds_seq), so a Seq-IN
        // lookup returns exactly one row per config — no per-config join
        // dance needed.
        var maxSeqs = stats.Select(s => s.LastSeq).ToArray();
        var lastBuilds = maxSeqs.Length == 0
            ? new List<LastBuildBits>()
            : await db.Builds.AsNoTracking()
                .Where(b => configIds.Contains(b.BuildConfigId) && maxSeqs.Contains(b.Seq))
                .Select(b => new LastBuildBits(b.BuildConfigId, b.Id, b.Seq, b.Outcome, b.DurationNs, b.StartedUnixNs))
                .ToListAsync(cancel).ConfigureAwait(false);
        var lastByConfig = lastBuilds.ToDictionary(b => b.ConfigId);

        var cpuByConfig = await db.Targets.AsNoTracking()
            .Where(t => configIds.Contains(t.Build.BuildConfigId))
            .GroupBy(t => t.Build.BuildConfigId)
            .Select(g => new { ConfigId = g.Key, CpuMs = g.Sum(t => t.CpuTimeMs) })
            .ToDictionaryAsync(x => x.ConfigId, x => x.CpuMs, cancel).ConfigureAwait(false);

        var totalsByConfig = stats.ToDictionary(s => s.ConfigId, s => s.TotalBuilds);

        var rows = configs.Select(c => new ConfigSummary
        {
            Slug = c.Slug,
            Name = c.Name,
            Description = c.Description,
            CreatedAt = c.CreatedAt,
            ArchivedAt = c.ArchivedAt,
            TotalBuilds = totalsByConfig.TryGetValue(c.Id, out var total) ? total : 0,
            CpuTimeMsSum = cpuByConfig.TryGetValue(c.Id, out var cpu) ? cpu : 0,
            LastBuild = lastByConfig.TryGetValue(c.Id, out var lb)
                ? new LastBuildSummary
                {
                    BuildId = lb.BuildId,
                    Seq = lb.Seq,
                    Outcome = lb.Outcome,
                    DurationNs = lb.DurationNs,
                    StartedUnixNs = lb.StartedUnixNs,
                }
                : null,
        }).ToList();
        return Results.Ok(new { configs = rows });
    }

    // ─── POST /api/projects/{slug}/configs ────────────────────────────────

    private static async Task<IResult> CreateAsync(
        string slug,
        CreateConfigRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        if (!ConfigSlugFormat.IsValid(body.Slug, out var slugError))
            return Results.BadRequest(new { error = slugError });
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { error = "name must not be empty" });

        var configSlug = body.Slug.Trim().ToLowerInvariant();
        var existing = await db.BuildConfigs.AsNoTracking()
            .AnyAsync(c => c.ProjectId == gate.Project!.Id && c.Slug == configSlug, cancel)
            .ConfigureAwait(false);
        if (existing) return Results.Conflict(new { error = "slug already in use within this project" });

        var now = DateTimeOffset.UtcNow;
        var config = new BuildConfig
        {
            ProjectId = gate.Project!.Id,
            Slug = configSlug,
            Name = body.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
            CreatedAt = now,
        };
        db.BuildConfigs.Add(config);
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);
        return Results.Created(
            $"/api/projects/{slug}/configs/{configSlug}",
            BasicSummary(config));
    }

    // ─── GET /api/projects/{slug}/configs/{configSlug} ────────────────────

    private static async Task<IResult> GetAsync(
        string slug,
        string configSlug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var (gate, config) = await GateAsync(ctx, slug, configSlug, ProjectRole.Viewer, auth, db, cancel).ConfigureAwait(false);
        if (gate is not null) return gate;

        return Results.Ok(BasicSummary(config!));
    }

    // ─── PATCH /api/projects/{slug}/configs/{configSlug} ──────────────────

    private static async Task<IResult> UpdateAsync(
        string slug,
        string configSlug,
        UpdateConfigRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var (gate, config) = await GateAsync(ctx, slug, configSlug, ProjectRole.Admin, auth, db, cancel).ConfigureAwait(false);
        if (gate is not null) return gate;

        if (!string.IsNullOrWhiteSpace(body.Name)) config!.Name = body.Name.Trim();
        if (body.Description is not null)
            config!.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);
        return Results.Ok(BasicSummary(config!));
    }

    // ─── DELETE /api/projects/{slug}/configs/{configSlug} — soft-archive ──

    private static async Task<IResult> ArchiveAsync(
        string slug,
        string configSlug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var (gate, config) = await GateAsync(ctx, slug, configSlug, ProjectRole.Admin, auth, db, cancel).ConfigureAwait(false);
        if (gate is not null) return gate;

        config!.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);
        return Results.NoContent();
    }

    // ─── helpers ───────────────────────────────────────────────────────────

    internal static async Task<(IResult? failure, BuildConfig? config)> GateAsync(
        HttpContext ctx,
        string slug,
        string configSlug,
        ProjectRole minRole,
        ProjectAuthorization auth,
        BeaconDbContext db,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, minRole, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return (ProjectsEndpoints.Map(gate), null);

        var normalized = configSlug?.Trim().ToLowerInvariant() ?? "";
        var config = await db.BuildConfigs
            .FirstOrDefaultAsync(c => c.ProjectId == gate.Project!.Id && c.Slug == normalized && c.ArchivedAt == null, cancel)
            .ConfigureAwait(false);
        if (config is null) return (Results.NotFound(new { error = "build config not found" }), null);
        return (null, config);
    }

    private static ConfigSummary BasicSummary(BuildConfig c) => new()
    {
        Slug = c.Slug,
        Name = c.Name,
        Description = c.Description,
        CreatedAt = c.CreatedAt,
        ArchivedAt = c.ArchivedAt,
        TotalBuilds = 0,
        CpuTimeMsSum = 0,
        LastBuild = null,
    };

    private sealed record LastBuildBits(long ConfigId, long BuildId, long Seq, string Outcome, long DurationNs, long StartedUnixNs);

    public sealed record CreateConfigRequest
    {
        [JsonPropertyName("slug")] public string Slug { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    public sealed record UpdateConfigRequest
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    public sealed record ConfigSummary
    {
        [JsonPropertyName("slug")] public string Slug { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
        [JsonPropertyName("archived_at")] public DateTimeOffset? ArchivedAt { get; init; }
        [JsonPropertyName("total_builds")] public int TotalBuilds { get; init; }
        [JsonPropertyName("cpu_time_ms_sum")] public double CpuTimeMsSum { get; init; }
        [JsonPropertyName("last_build")] public LastBuildSummary? LastBuild { get; init; }
    }

    public sealed record LastBuildSummary
    {
        [JsonPropertyName("build_id")] public long BuildId { get; init; }
        [JsonPropertyName("seq")] public long Seq { get; init; }
        [JsonPropertyName("outcome")] public string Outcome { get; init; } = "";
        [JsonPropertyName("duration_ns")] public long DurationNs { get; init; }
        [JsonPropertyName("started_unix_ns")] public long StartedUnixNs { get; init; }
    }
}

internal static class ConfigSlugFormat
{
    public static bool IsValid(string? slug, out string error)
    {
        if (string.IsNullOrWhiteSpace(slug)) { error = "slug is required"; return false; }
        var trimmed = slug.Trim();
        if (trimmed.Length is < 2 or > 64) { error = "config slug must be 2-64 characters"; return false; }
        foreach (var c in trimmed)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok) { error = "config slug must contain only lowercase a-z, 0-9, and hyphen"; return false; }
        }
        if (trimmed[0] == '-' || trimmed[^1] == '-')
        { error = "config slug must not begin or end with a hyphen"; return false; }
        error = "";
        return true;
    }
}
