using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Api;

/// <summary>
/// Endpoint surface for slice 2 — cookie-backed local login
/// (<c>/break-glass</c>), logout, current-principal probe (<c>/me</c>),
/// GitHub OAuth entry (<c>/signin/github</c>), and the admin-recovery
/// reset-token consumer (<c>/admin/recover</c>).
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/break-glass", BreakGlassAsync).RequireRateLimiting("break-glass");
        app.MapPost("/logout", LogoutAsync);
        app.MapGet("/me", MeAsync);
        app.MapGet("/signin/github", SigninGitHubAsync);
        app.MapPost("/admin/recover", AdminRecoverAsync);
        return app;
    }

    private static async Task<IResult> BreakGlassAsync(
        BreakGlassRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        PasswordHasher hasher,
        CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
        {
            return Results.BadRequest(new { error = "username and password are required" });
        }

        var username = body.Username.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, cancel).ConfigureAwait(false);

        if (user is null || user.IsDisabled || string.IsNullOrEmpty(user.PasswordHash))
        {
            await AuditAsync(db, "auth.login_failure.unknown_user", ctx, null, cancel).ConfigureAwait(false);
            return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!hasher.Verify(body.Password, user.PasswordHash))
        {
            await AuditAsync(db, "auth.login_failure.bad_password", ctx, user.Id, cancel).ConfigureAwait(false);
            return Results.Json(new { error = "invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await AuditAsync(db, "auth.login_success.break_glass", ctx, user.Id, cancel).ConfigureAwait(false);

        var principal = BuildPrincipal(user);
        await ctx.SignInAsync(AuthExtensions.CookieScheme, principal,
            new AuthenticationProperties { IsPersistent = true }).ConfigureAwait(false);

        return Results.Ok(SessionResponse.From(user));
    }

    private static async Task<IResult> LogoutAsync(HttpContext ctx, BeaconDbContext db, CancellationToken cancel)
    {
        var userIdClaim = ctx.User.FindFirstValue(AuthExtensions.UserIdClaim);
        long.TryParse(userIdClaim, out var uid);
        await ctx.SignOutAsync(AuthExtensions.CookieScheme).ConfigureAwait(false);
        await AuditAsync(db, "auth.logout", ctx, uid == 0 ? null : uid, cancel).ConfigureAwait(false);
        return Results.Ok(new { status = "signed out" });
    }

    private static async Task<IResult> MeAsync(HttpContext ctx, BeaconDbContext db, CancellationToken cancel)
    {
        if (!(ctx.User?.Identity?.IsAuthenticated ?? false))
        {
            return Results.Json(new { error = "not signed in" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        var userIdClaim = ctx.User.FindFirstValue(AuthExtensions.UserIdClaim);
        if (!long.TryParse(userIdClaim, out var uid))
        {
            return Results.Json(new { error = "principal missing user id" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == uid, cancel).ConfigureAwait(false);
        if (user is null || user.IsDisabled)
        {
            return Results.Json(new { error = "user no longer active" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        return Results.Ok(SessionResponse.From(user));
    }

    private static Task<IResult> SigninGitHubAsync(HttpContext ctx, IOptions<AuthOptions> opts, string? returnUrl)
    {
        if (!opts.Value.GitHub.IsConfigured)
        {
            return Task.FromResult(Results.NotFound(new { error = "GitHub OAuth is not configured" }));
        }
        var props = new AuthenticationProperties
        {
            RedirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl,
        };
        return Task.FromResult(Results.Challenge(props, new[] { AuthExtensions.GitHubScheme }));
    }

    private static async Task<IResult> AdminRecoverAsync(
        AdminRecoverRequest body,
        HttpContext ctx,
        PasswordResetService reset,
        BeaconDbContext db,
        CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(body.Username) ||
            string.IsNullOrWhiteSpace(body.Token) ||
            string.IsNullOrWhiteSpace(body.NewPassword))
        {
            return Results.BadRequest(new { error = "username, token, and new_password are required" });
        }

        var username = body.Username.Trim().ToLowerInvariant();
        var result = await reset.ConsumeAsync(username, body.Token, body.NewPassword, cancel).ConfigureAwait(false);
        return result switch
        {
            PasswordResetService.ConsumeResult.Success => Results.Ok(new { status = "password reset" }),
            PasswordResetService.ConsumeResult.PasswordTooShort =>
                Results.BadRequest(new { error = "new_password must be at least 12 characters" }),
            PasswordResetService.ConsumeResult.UserNotFoundOrDisabled =>
                Results.Json(new { error = "invalid recovery token" }, statusCode: StatusCodes.Status401Unauthorized),
            PasswordResetService.ConsumeResult.NoPendingReset =>
                Results.Json(new { error = "invalid recovery token" }, statusCode: StatusCodes.Status401Unauthorized),
            PasswordResetService.ConsumeResult.TokenMismatch =>
                Results.Json(new { error = "invalid recovery token" }, statusCode: StatusCodes.Status401Unauthorized),
            PasswordResetService.ConsumeResult.TokenExpired =>
                Results.Json(new { error = "recovery token expired — rerun `tamp-beacon admin recover` to mint a fresh one" },
                    statusCode: StatusCodes.Status401Unauthorized),
            _ => Results.BadRequest(new { error = "invalid request" }),
        };
    }

    private static ClaimsPrincipal BuildPrincipal(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(AuthExtensions.UserIdClaim, user.Id.ToString()),
            new Claim(AuthExtensions.DisplayNameClaim, user.DisplayName),
        };
        var identity = new ClaimsIdentity(claims, AuthExtensions.CookieScheme,
            ClaimTypes.Name, ClaimTypes.Role);
        if (user.IsSystemAdmin)
        {
            identity.AddClaim(new Claim(AuthExtensions.SystemAdminClaim, "true"));
        }
        return new ClaimsPrincipal(identity);
    }

    private static async Task AuditAsync(BeaconDbContext db, string evt, HttpContext ctx, long? actorUserId, CancellationToken ct)
    {
        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = evt,
            ActorUserId = actorUserId,
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public sealed record BreakGlassRequest
    {
        [JsonPropertyName("username")] public string Username { get; init; } = "";
        [JsonPropertyName("password")] public string Password { get; init; } = "";
    }

    public sealed record AdminRecoverRequest
    {
        [JsonPropertyName("username")] public string Username { get; init; } = "";
        [JsonPropertyName("token")] public string Token { get; init; } = "";
        [JsonPropertyName("new_password")] public string NewPassword { get; init; } = "";
    }

    public sealed record SessionResponse
    {
        [JsonPropertyName("username")] public string Username { get; init; } = "";
        [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "";
        [JsonPropertyName("is_system_admin")] public bool IsSystemAdmin { get; init; }
        [JsonPropertyName("last_login_at")] public DateTimeOffset? LastLoginAt { get; init; }

        public static SessionResponse From(User u) => new()
        {
            Username = u.Username,
            DisplayName = u.DisplayName,
            IsSystemAdmin = u.IsSystemAdmin,
            LastLoginAt = u.LastLoginAt,
        };
    }
}
