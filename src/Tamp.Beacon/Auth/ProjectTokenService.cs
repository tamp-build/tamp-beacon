using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Mints + revokes per-project ingest tokens. Plaintext is returned exactly
/// once from <see cref="MintAsync"/>; only the sha256 hex is persisted so a
/// DB snapshot leak doesn't expose live tokens. Slice 4 will resolve
/// presented bearer tokens by hash via <see cref="ResolveAsync"/> on the
/// OTLP ingest path.
/// </summary>
public sealed class ProjectTokenService
{
    private readonly BeaconDbContext _db;

    public ProjectTokenService(BeaconDbContext db) => _db = db;

    public async Task<MintedToken> MintAsync(
        long projectId,
        long createdByUserId,
        string label,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("label must not be empty", nameof(label));

        var plaintext = $"tbk_{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        var row = new ProjectToken
        {
            ProjectId = projectId,
            CreatedByUserId = createdByUserId,
            Label = label.Trim(),
            TokenHash = SetupTokenManager.Sha256Hex(plaintext),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.ProjectTokens.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MintedToken(row, plaintext);
    }

    /// <summary>
    /// Look up an active token by plaintext (slice 4 will call this on
    /// every OTLP request). Returns null when the token doesn't exist or
    /// has been revoked.
    /// </summary>
    public async Task<ProjectToken?> ResolveAsync(string plaintext, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintext)) return null;
        var hash = SetupTokenManager.Sha256Hex(plaintext);
        return await _db.ProjectTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Mark a token revoked. Idempotent — second call no-ops.</summary>
    public async Task<bool> RevokeAsync(long tokenId, CancellationToken ct = default)
    {
        var row = await _db.ProjectTokens.FirstOrDefaultAsync(t => t.Id == tokenId, ct).ConfigureAwait(false);
        if (row is null) return false;
        if (row.RevokedAt is null)
        {
            row.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public sealed record MintedToken(ProjectToken Row, string Plaintext);
}
