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
/// System-admin-only surface for user roster + sysadmin role management.
/// The "min one sysadmin" invariant is enforced at the demote endpoint —
/// a malformed PATCH that would leave zero sysadmins is rejected with 409.
/// </summary>
public static class AdminUsersEndpoints
{
    public static IEndpointRouteBuilder MapAdminUsers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/users").RequireAuthorization();

        group.MapGet("", ListAsync);
        group.MapPost("/{username}/promote", PromoteAsync);
        group.MapPost("/{username}/demote", DemoteAsync);
        group.MapPost("/{username}/disable", DisableAsync);
        group.MapPost("/{username}/enable", EnableAsync);
        return app;
    }

    private static IResult? RequireSysadmin(HttpContext ctx)
    {
        if (!ProjectAuthorization.IsSystemAdmin(ctx))
        {
            return Results.Json(new { error = "system admin required" },
                statusCode: StatusCodes.Status403Forbidden);
        }
        return null;
    }

    private static async Task<IResult> ListAsync(
        HttpContext ctx,
        BeaconDbContext db,
        CancellationToken cancel)
    {
        if (RequireSysadmin(ctx) is { } gate) return gate;

        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new UserSummary
            {
                Username = u.Username,
                DisplayName = u.DisplayName,
                IsSystemAdmin = u.IsSystemAdmin,
                IsDisabled = u.IsDisabled,
                HasPassword = u.PasswordHash != null,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
            })
            .ToListAsync(cancel).ConfigureAwait(false);
        return Results.Ok(new { users });
    }

    private static async Task<IResult> PromoteAsync(
        string username,
        HttpContext ctx,
        BeaconDbContext db,
        CancellationToken cancel)
    {
        if (RequireSysadmin(ctx) is { } gate) return gate;
        var user = await FindAsync(db, username, cancel).ConfigureAwait(false);
        if (user is null) return Results.NotFound(new { error = "user not found" });
        if (user.IsSystemAdmin) return Results.Ok(new { status = "already a system admin" });

        user.IsSystemAdmin = true;
        await AuditAsync(db, "admin.user_promoted", ctx, user, cancel).ConfigureAwait(false);
        return Results.Ok(new { status = "promoted" });
    }

    private static async Task<IResult> DemoteAsync(
        string username,
        HttpContext ctx,
        BeaconDbContext db,
        CancellationToken cancel)
    {
        if (RequireSysadmin(ctx) is { } gate) return gate;
        var user = await FindAsync(db, username, cancel).ConfigureAwait(false);
        if (user is null) return Results.NotFound(new { error = "user not found" });
        if (!user.IsSystemAdmin) return Results.Ok(new { status = "already not a system admin" });

        var adminCount = await db.Users.CountAsync(u => u.IsSystemAdmin && !u.IsDisabled, cancel)
            .ConfigureAwait(false);
        if (adminCount <= 1)
            return Results.Conflict(new { error = "cannot demote the last system admin" });

        user.IsSystemAdmin = false;
        await AuditAsync(db, "admin.user_demoted", ctx, user, cancel).ConfigureAwait(false);
        return Results.Ok(new { status = "demoted" });
    }

    private static async Task<IResult> DisableAsync(
        string username,
        HttpContext ctx,
        BeaconDbContext db,
        CancellationToken cancel)
    {
        if (RequireSysadmin(ctx) is { } gate) return gate;
        var user = await FindAsync(db, username, cancel).ConfigureAwait(false);
        if (user is null) return Results.NotFound(new { error = "user not found" });
        if (user.IsDisabled) return Results.Ok(new { status = "already disabled" });

        // Prevent locking out the last enabled sysadmin.
        if (user.IsSystemAdmin)
        {
            var enabledAdminCount = await db.Users.CountAsync(u => u.IsSystemAdmin && !u.IsDisabled, cancel)
                .ConfigureAwait(false);
            if (enabledAdminCount <= 1)
                return Results.Conflict(new { error = "cannot disable the last enabled system admin" });
        }

        user.IsDisabled = true;
        await AuditAsync(db, "admin.user_disabled", ctx, user, cancel).ConfigureAwait(false);
        return Results.Ok(new { status = "disabled" });
    }

    private static async Task<IResult> EnableAsync(
        string username,
        HttpContext ctx,
        BeaconDbContext db,
        CancellationToken cancel)
    {
        if (RequireSysadmin(ctx) is { } gate) return gate;
        var user = await FindAsync(db, username, cancel).ConfigureAwait(false);
        if (user is null) return Results.NotFound(new { error = "user not found" });
        if (!user.IsDisabled) return Results.Ok(new { status = "already enabled" });
        user.IsDisabled = false;
        await AuditAsync(db, "admin.user_enabled", ctx, user, cancel).ConfigureAwait(false);
        return Results.Ok(new { status = "enabled" });
    }

    private static Task<User?> FindAsync(BeaconDbContext db, string username, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Username == username.Trim().ToLowerInvariant(), ct);

    private static async Task AuditAsync(BeaconDbContext db, string evt, HttpContext ctx, User target, CancellationToken ct)
    {
        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = evt,
            ActorUserId = ProjectAuthorization.CurrentUserId(ctx),
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = DateTimeOffset.UtcNow,
            DetailJson = $"{{\"target_username\":\"{target.Username}\"}}",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public sealed record UserSummary
    {
        [JsonPropertyName("username")] public string Username { get; init; } = "";
        [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "";
        [JsonPropertyName("is_system_admin")] public bool IsSystemAdmin { get; init; }
        [JsonPropertyName("is_disabled")] public bool IsDisabled { get; init; }
        [JsonPropertyName("has_password")] public bool HasPassword { get; init; }
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
        [JsonPropertyName("last_login_at")] public DateTimeOffset? LastLoginAt { get; init; }
    }
}
