using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Mints + validates one-shot password-reset tokens. The CLI subcommand
/// <c>tamp-beacon admin recover --username NAME</c> calls
/// <see cref="MintAsync"/>; the <c>POST /admin/recover</c> endpoint calls
/// <see cref="ConsumeAsync"/>. Only the SHA-256 hash is persisted —
/// presenting the plaintext token is the only handle.
/// </summary>
public sealed class PasswordResetService
{
    private readonly BeaconDbContext _db;
    private readonly AuthOptions _opts;
    private readonly PasswordHasher _hasher;

    public PasswordResetService(BeaconDbContext db, IOptions<AuthOptions> opts, PasswordHasher hasher)
    {
        _db = db;
        _opts = opts.Value;
        _hasher = hasher;
    }

    /// <summary>
    /// Mint a fresh reset token for <paramref name="username"/>. Returns the
    /// plaintext token (caller is responsible for displaying it). Returns
    /// null if the user does not exist or is disabled.
    /// </summary>
    public async Task<string?> MintAsync(string username, CancellationToken ct = default)
    {
        var user = await FindByUsernameAsync(username, ct).ConfigureAwait(false);
        if (user is null || user.IsDisabled) return null;

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Base64Url(bytes);
        user.PendingResetHash = SetupTokenManager.Sha256Hex(token);
        user.PendingResetIssuedAt = DateTimeOffset.UtcNow;

        _db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "admin.password_reset_token_minted",
            ActorUserId = user.Id,
            AtUtc = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return token;
    }

    /// <summary>
    /// Consume <paramref name="token"/> against <paramref name="username"/>'s
    /// pending reset and replace their password hash with one over
    /// <paramref name="newPassword"/>. Returns a non-null reason string on
    /// failure (already mapped to the right HTTP response by the caller).
    /// </summary>
    public async Task<ConsumeResult> ConsumeAsync(
        string username, string token, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(token))
            return ConsumeResult.InvalidArguments;
        if (newPassword.Length < 12)
            return ConsumeResult.PasswordTooShort;

        var user = await FindByUsernameAsync(username, ct).ConfigureAwait(false);
        if (user is null || user.IsDisabled) return ConsumeResult.UserNotFoundOrDisabled;

        if (string.IsNullOrEmpty(user.PendingResetHash) || user.PendingResetIssuedAt is null)
            return ConsumeResult.NoPendingReset;

        var presented = SetupTokenManager.Sha256Hex(token);
        if (!FixedTimeEquals(presented, user.PendingResetHash))
            return ConsumeResult.TokenMismatch;

        var ttl = TimeSpan.FromSeconds(_opts.PasswordResetTokenTtlSeconds);
        if ((DateTimeOffset.UtcNow - user.PendingResetIssuedAt.Value) > ttl)
            return ConsumeResult.TokenExpired;

        user.PasswordHash = _hasher.Hash(newPassword);
        user.PendingResetHash = null;
        user.PendingResetIssuedAt = null;

        _db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "admin.password_reset_consumed",
            ActorUserId = user.Id,
            AtUtc = DateTimeOffset.UtcNow,
        });

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ConsumeResult.Success;
    }

    private Task<User?> FindByUsernameAsync(string username, CancellationToken ct)
    {
        var u = username.Trim();
        return _db.Users.FirstOrDefaultAsync(x => x.Username == u, ct);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public enum ConsumeResult
    {
        Success,
        InvalidArguments,
        PasswordTooShort,
        UserNotFoundOrDisabled,
        NoPendingReset,
        TokenMismatch,
        TokenExpired,
    }
}
