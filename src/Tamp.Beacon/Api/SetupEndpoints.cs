using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Api;

/// <summary>
/// First-run bootstrap endpoint. Accepts the stdout-printed setup token plus
/// the desired initial admin credentials and, when valid, mints the first
/// <see cref="User"/> row + flips <see cref="SetupState.IsComplete"/> to
/// true. Once complete the endpoint refuses all further requests until the
/// database is reset.
/// </summary>
public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetup(this IEndpointRouteBuilder app)
    {
        app.MapGet("/setup/status", async (BeaconDbContext db, CancellationToken ct) =>
        {
            var state = await db.SetupStateEntries.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
            var complete = state?.IsComplete ?? false;
            return Results.Ok(new SetupStatusResponse
            {
                AwaitingSetup = !complete,
                IsComplete = complete,
                TokenIssuedAt = state?.PendingTokenIssuedAt,
            });
        });

        app.MapPost("/setup", async (
            SetupRequest body,
            HttpContext ctx,
            BeaconDbContext db,
            PasswordHasher hasher,
            IOptions<AuthOptions> authOpts,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token) ||
                string.IsNullOrWhiteSpace(body.Username) ||
                string.IsNullOrWhiteSpace(body.Password))
            {
                return Results.BadRequest(new { error = "token, username, and password are required" });
            }

            var state = await db.SetupStateEntries.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);
            if (state is null)
            {
                return Results.Json(new { error = "setup state row missing — readiness probe should be 503" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (state.IsComplete)
            {
                await AuditAsync(db, "setup.attempt_after_complete", ctx, null, ct).ConfigureAwait(false);
                return Results.Json(new { error = "setup already complete" }, statusCode: StatusCodes.Status409Conflict);
            }

            var presentedHash = SetupTokenManager.Sha256Hex(body.Token);
            if (string.IsNullOrEmpty(state.PendingTokenHash) ||
                !FixedTimeEquals(presentedHash, state.PendingTokenHash))
            {
                await AuditAsync(db, "setup.invalid_token", ctx, null, ct).ConfigureAwait(false);
                return Results.Json(new { error = "invalid setup token" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var issued = state.PendingTokenIssuedAt;
            var ttl = TimeSpan.FromSeconds(authOpts.Value.SetupTokenTtlSeconds);
            if (issued is null || (DateTimeOffset.UtcNow - issued.Value) > ttl)
            {
                await AuditAsync(db, "setup.expired_token", ctx, null, ct).ConfigureAwait(false);
                return Results.Json(new { error = "setup token expired — restart the pod to mint a fresh one" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var trimmedUsername = body.Username.Trim();
            if (!IsValidUsername(trimmedUsername))
            {
                return Results.BadRequest(new { error = "username must be 2-64 chars of a-z 0-9 . _ -" });
            }
            var username = trimmedUsername;
            if (body.Password.Length < 12)
            {
                return Results.BadRequest(new { error = "password must be at least 12 characters" });
            }

            var now = DateTimeOffset.UtcNow;
            var admin = new User
            {
                Username = username,
                DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? username : body.DisplayName.Trim(),
                PasswordHash = hasher.Hash(body.Password),
                IsSystemAdmin = true,
                CreatedAt = now,
            };
            db.Users.Add(admin);

            state.IsComplete = true;
            state.CompletedAt = now;
            state.PendingTokenHash = null;
            state.PendingTokenIssuedAt = null;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(db, "setup.completed", ctx, admin.Id, ct).ConfigureAwait(false);

            return Results.Ok(new SetupResponse
            {
                Username = admin.Username,
                DisplayName = admin.DisplayName,
                CreatedAt = admin.CreatedAt,
            });
        });

        return app;
    }

    private static bool IsValidUsername(string s)
    {
        if (s.Length is < 2 or > 64) return false;
        foreach (var c in s)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '.' or '_' or '-';
            if (!ok) return false;
        }
        return true;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
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

    public sealed record SetupRequest
    {
        [JsonPropertyName("token")] public string Token { get; init; } = "";
        [JsonPropertyName("username")] public string Username { get; init; } = "";
        [JsonPropertyName("password")] public string Password { get; init; } = "";
        [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    }

    public sealed record SetupResponse
    {
        [JsonPropertyName("username")] public string Username { get; init; } = "";
        [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "";
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
    }

    public sealed record SetupStatusResponse
    {
        [JsonPropertyName("awaiting_setup")] public bool AwaitingSetup { get; init; }
        [JsonPropertyName("is_complete")] public bool IsComplete { get; init; }
        [JsonPropertyName("token_issued_at")] public DateTimeOffset? TokenIssuedAt { get; init; }
    }
}
