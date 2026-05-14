using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Api;

/// <summary>
/// Project CRUD + RBAC. Endpoints return 404 (not 403) when the caller has
/// no membership, hiding project existence from non-members — TAM-214
/// threat-model decision matching SonarQube semantics.
/// </summary>
public static class ProjectsEndpoints
{
    public static IEndpointRouteBuilder MapProjects(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapPost("", CreateAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{slug}", GetAsync);
        group.MapPatch("/{slug}", UpdateAsync);
        group.MapDelete("/{slug}", ArchiveAsync);
        return app;
    }

    // ─── POST /api/projects ─────────────────────────────────────────────

    private static async Task<IResult> CreateAsync(
        CreateProjectRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        CancellationToken cancel)
    {
        var userId = ProjectAuthorization.CurrentUserId(ctx);
        if (userId is null) return Results.Unauthorized();

        if (!SlugFormat.IsValid(body.Slug, out var slugError))
            return Results.BadRequest(new { error = slugError });
        if (string.IsNullOrWhiteSpace(body.Name))
            return Results.BadRequest(new { error = "name must not be empty" });

        var slug = body.Slug.Trim().ToLowerInvariant();
        var existing = await db.Projects.AsNoTracking()
            .AnyAsync(p => p.Slug == slug, cancel).ConfigureAwait(false);
        if (existing) return Results.Conflict(new { error = "slug already in use" });

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Slug = slug,
            Name = body.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
            CreatedByUserId = userId.Value,
            CreatedAt = now,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);

        // Creator gets project admin role automatically.
        db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = project.Id,
            UserId = userId.Value,
            Role = ProjectRole.Admin,
            AddedAt = now,
        });
        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "project.created",
            ActorUserId = userId.Value,
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = now,
            DetailJson = $"{{\"slug\":\"{slug}\"}}",
        });
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);

        return Results.Created($"/api/projects/{slug}", ProjectSummary.From(project, memberCount: 1, myRole: ProjectRole.Admin));
    }

    // ─── GET /api/projects ───────────────────────────────────────────────

    private static async Task<IResult> ListAsync(
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var userId = ProjectAuthorization.CurrentUserId(ctx);
        if (userId is null) return Results.Unauthorized();

        var visibleIds = await auth.VisibleProjectIdsAsync(ctx, cancel).ConfigureAwait(false);

        var rows = await (
            from p in db.Projects.AsNoTracking()
            where visibleIds.Contains(p.Id) && p.ArchivedAt == null
            select new
            {
                Project = p,
                MemberCount = p.Members.Count,
                MyMembership = p.Members.FirstOrDefault(m => m.UserId == userId.Value),
            }).ToListAsync(cancel).ConfigureAwait(false);

        var sysadmin = ProjectAuthorization.IsSystemAdmin(ctx);
        var summaries = rows.Select(r => ProjectSummary.From(
            r.Project,
            r.MemberCount,
            r.MyMembership?.Role ?? (sysadmin ? ProjectRole.Admin : ProjectRole.Viewer))).ToList();

        return Results.Ok(new { projects = summaries });
    }

    // ─── GET /api/projects/{slug} ────────────────────────────────────────

    private static async Task<IResult> GetAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return Map(gate);

        var memberCount = await db.ProjectMembers.AsNoTracking()
            .CountAsync(m => m.ProjectId == gate.Project!.Id, cancel).ConfigureAwait(false);

        return Results.Ok(ProjectSummary.From(gate.Project!, memberCount, gate.EffectiveRole));
    }

    // ─── PATCH /api/projects/{slug} ──────────────────────────────────────

    private static async Task<IResult> UpdateAsync(
        string slug,
        UpdateProjectRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return Map(gate);

        var project = await db.Projects.FirstAsync(p => p.Id == gate.Project!.Id, cancel).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(body.Name)) project.Name = body.Name.Trim();
        if (body.Description is not null) project.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);

        var memberCount = await db.ProjectMembers.AsNoTracking()
            .CountAsync(m => m.ProjectId == project.Id, cancel).ConfigureAwait(false);
        return Results.Ok(ProjectSummary.From(project, memberCount, gate.EffectiveRole));
    }

    // ─── DELETE /api/projects/{slug} — archive (soft delete) ─────────────

    private static async Task<IResult> ArchiveAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return Map(gate);

        var project = await db.Projects.FirstAsync(p => p.Id == gate.Project!.Id, cancel).ConfigureAwait(false);
        project.ArchivedAt = DateTimeOffset.UtcNow;

        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "project.archived",
            ActorUserId = ProjectAuthorization.CurrentUserId(ctx),
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = DateTimeOffset.UtcNow,
            DetailJson = $"{{\"slug\":\"{project.Slug}\"}}",
        });
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);
        return Results.NoContent();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    internal static IResult Map(GateResult gate) => gate.Gate switch
    {
        ProjectGate.Unauthenticated => Results.Unauthorized(),
        ProjectGate.Forbidden => Results.Json(new { error = "insufficient project privileges" },
            statusCode: StatusCodes.Status403Forbidden),
        _ => Results.NotFound(new { error = "project not found" }),
    };

    public sealed record CreateProjectRequest
    {
        [JsonPropertyName("slug")] public string Slug { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    public sealed record UpdateProjectRequest
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
    }

    public sealed record ProjectSummary
    {
        [JsonPropertyName("slug")] public string Slug { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
        [JsonPropertyName("archived_at")] public DateTimeOffset? ArchivedAt { get; init; }
        [JsonPropertyName("member_count")] public int MemberCount { get; init; }
        [JsonPropertyName("my_role")] public string MyRole { get; init; } = "";

        public static ProjectSummary From(Project p, int memberCount, ProjectRole myRole) => new()
        {
            Slug = p.Slug,
            Name = p.Name,
            Description = p.Description,
            CreatedAt = p.CreatedAt,
            ArchivedAt = p.ArchivedAt,
            MemberCount = memberCount,
            MyRole = myRole.ToString().ToLowerInvariant(),
        };
    }
}

internal static class SlugFormat
{
    public static bool IsValid(string? slug, out string error)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            error = "slug is required";
            return false;
        }
        var trimmed = slug.Trim();
        if (trimmed.Length is < 2 or > 64)
        {
            error = "slug must be 2-64 characters";
            return false;
        }
        foreach (var c in trimmed)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok)
            {
                error = "slug must contain only lowercase a-z, 0-9, and hyphen";
                return false;
            }
        }
        if (trimmed[0] == '-' || trimmed[^1] == '-')
        {
            error = "slug must not begin or end with a hyphen";
            return false;
        }
        error = "";
        return true;
    }
}
