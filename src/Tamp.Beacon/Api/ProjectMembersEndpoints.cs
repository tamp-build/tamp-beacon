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

public static class ProjectMembersEndpoints
{
    public static IEndpointRouteBuilder MapProjectMembers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{slug}/members").RequireAuthorization();

        group.MapGet("", ListAsync);
        group.MapPost("", AddAsync);
        group.MapPatch("/{memberId:long}", UpdateRoleAsync);
        group.MapDelete("/{memberId:long}", RemoveAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        // Any member can see the membership roster — basic visibility check.
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var rows = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == gate.Project!.Id)
            .Select(m => new MemberSummary
            {
                Id = m.Id,
                Username = m.User.Username,
                DisplayName = m.User.DisplayName,
                Role = m.Role.ToString().ToLowerInvariant(),
                AddedAt = m.AddedAt,
            })
            .ToListAsync(cancel).ConfigureAwait(false);
        return Results.Ok(new { members = rows });
    }

    private static async Task<IResult> AddAsync(
        string slug,
        AddMemberRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        if (string.IsNullOrWhiteSpace(body.Username))
            return Results.BadRequest(new { error = "username is required" });
        if (!TryParseRole(body.Role, out var role))
            return Results.BadRequest(new { error = "role must be 'admin' or 'viewer'" });

        var username = body.Username.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, cancel).ConfigureAwait(false);
        if (user is null || user.IsDisabled)
            return Results.NotFound(new { error = "user not found" });

        var existing = await db.ProjectMembers
            .FirstOrDefaultAsync(m => m.ProjectId == gate.Project!.Id && m.UserId == user.Id, cancel)
            .ConfigureAwait(false);
        if (existing is not null)
            return Results.Conflict(new { error = "user is already a member of this project" });

        var member = new ProjectMember
        {
            ProjectId = gate.Project!.Id,
            UserId = user.Id,
            Role = role,
            AddedAt = DateTimeOffset.UtcNow,
        };
        db.ProjectMembers.Add(member);
        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "project.member_added",
            ActorUserId = ProjectAuthorization.CurrentUserId(ctx),
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = DateTimeOffset.UtcNow,
            DetailJson = $"{{\"slug\":\"{slug}\",\"username\":\"{username}\",\"role\":\"{role.ToString().ToLowerInvariant()}\"}}",
        });
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);

        return Results.Created($"/api/projects/{slug}/members/{member.Id}", new MemberSummary
        {
            Id = member.Id,
            Username = user.Username,
            DisplayName = user.DisplayName,
            Role = role.ToString().ToLowerInvariant(),
            AddedAt = member.AddedAt,
        });
    }

    private static async Task<IResult> UpdateRoleAsync(
        string slug,
        long memberId,
        UpdateRoleRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        if (!TryParseRole(body.Role, out var role))
            return Results.BadRequest(new { error = "role must be 'admin' or 'viewer'" });

        var member = await db.ProjectMembers
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.Id == memberId && m.ProjectId == gate.Project!.Id, cancel)
            .ConfigureAwait(false);
        if (member is null) return Results.NotFound(new { error = "member not found" });

        // Min-one-admin invariant — can't demote the last admin.
        if (member.Role == ProjectRole.Admin && role != ProjectRole.Admin)
        {
            var adminCount = await db.ProjectMembers.CountAsync(
                m => m.ProjectId == gate.Project!.Id && m.Role == ProjectRole.Admin, cancel).ConfigureAwait(false);
            if (adminCount <= 1)
                return Results.Conflict(new { error = "cannot demote the last project admin" });
        }

        member.Role = role;
        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "project.member_role_changed",
            ActorUserId = ProjectAuthorization.CurrentUserId(ctx),
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = DateTimeOffset.UtcNow,
            DetailJson = $"{{\"slug\":\"{slug}\",\"username\":\"{member.User.Username}\",\"role\":\"{role.ToString().ToLowerInvariant()}\"}}",
        });
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);

        return Results.Ok(new MemberSummary
        {
            Id = member.Id,
            Username = member.User.Username,
            DisplayName = member.User.DisplayName,
            Role = role.ToString().ToLowerInvariant(),
            AddedAt = member.AddedAt,
        });
    }

    private static async Task<IResult> RemoveAsync(
        string slug,
        long memberId,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var member = await db.ProjectMembers
            .Include(m => m.User)
            .FirstOrDefaultAsync(m => m.Id == memberId && m.ProjectId == gate.Project!.Id, cancel)
            .ConfigureAwait(false);
        if (member is null) return Results.NotFound(new { error = "member not found" });

        if (member.Role == ProjectRole.Admin)
        {
            var adminCount = await db.ProjectMembers.CountAsync(
                m => m.ProjectId == gate.Project!.Id && m.Role == ProjectRole.Admin, cancel).ConfigureAwait(false);
            if (adminCount <= 1)
                return Results.Conflict(new { error = "cannot remove the last project admin" });
        }

        db.ProjectMembers.Remove(member);
        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "project.member_removed",
            ActorUserId = ProjectAuthorization.CurrentUserId(ctx),
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = DateTimeOffset.UtcNow,
            DetailJson = $"{{\"slug\":\"{slug}\",\"username\":\"{member.User.Username}\"}}",
        });
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static bool TryParseRole(string? value, out ProjectRole role)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "admin": role = ProjectRole.Admin; return true;
            case "viewer": role = ProjectRole.Viewer; return true;
            default: role = default; return false;
        }
    }

    public sealed record AddMemberRequest
    {
        [JsonPropertyName("username")] public string Username { get; init; } = "";
        [JsonPropertyName("role")] public string Role { get; init; } = "viewer";
    }

    public sealed record UpdateRoleRequest
    {
        [JsonPropertyName("role")] public string Role { get; init; } = "";
    }

    public sealed record MemberSummary
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("username")] public string Username { get; init; } = "";
        [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "";
        [JsonPropertyName("role")] public string Role { get; init; } = "";
        [JsonPropertyName("added_at")] public DateTimeOffset AddedAt { get; init; }
    }
}
