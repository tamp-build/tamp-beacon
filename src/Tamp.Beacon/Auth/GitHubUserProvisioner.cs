using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Maps a GitHub user to a local <see cref="User"/> row + linked
/// <see cref="IdentityProviderLink"/>, creating both on first sign-in.
/// Idempotent — subsequent sign-ins refresh <see cref="User.LastLoginAt"/>
/// and the display name. Identity-provider row for GitHub is auto-created
/// on first use so adopters don't have to seed it.
/// </summary>
public sealed class GitHubUserProvisioner
{
    private readonly BeaconDbContext _db;

    public GitHubUserProvisioner(BeaconDbContext db) => _db = db;

    public async Task<User> EnsureUserAsync(
        string githubLogin,
        string githubSubject,
        string? displayName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(githubLogin))
            throw new ArgumentException("githubLogin must not be empty", nameof(githubLogin));
        if (string.IsNullOrWhiteSpace(githubSubject))
            throw new ArgumentException("githubSubject must not be empty", nameof(githubSubject));

        var provider = await EnsureProviderAsync(ct).ConfigureAwait(false);

        var existingLink = await _db.IdentityProviderLinks
            .Include(l => l.User)
            .FirstOrDefaultAsync(l => l.ProviderId == provider.Id && l.Subject == githubSubject, ct)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        if (existingLink is not null)
        {
            existingLink.User.LastLoginAt = now;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                existingLink.User.DisplayName = displayName!;
            }
            _db.AuthAuditLog.Add(new AuthAuditLogEntry
            {
                Event = "auth.login_success.github",
                ActorUserId = existingLink.User.Id,
                AtUtc = now,
            });
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return existingLink.User;
        }

        // No existing link by subject. If a local user already owns this
        // username, attach the GH link to that user — covers the case where
        // a setup-token admin (e.g. "scott") later signs in with a matching
        // GitHub login. Otherwise create a fresh user with no privileges.
        var loginLower = githubLogin.ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == loginLower, ct).ConfigureAwait(false);
        if (user is null)
        {
            user = new User
            {
                Username = loginLower,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? loginLower : displayName!,
                PasswordHash = null,
                IsSystemAdmin = false,
                IsDisabled = false,
                CreatedAt = now,
                LastLoginAt = now,
            };
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _db.AuthAuditLog.Add(new AuthAuditLogEntry
            {
                Event = "auth.user_provisioned.github",
                ActorUserId = user.Id,
                AtUtc = now,
            });
        }
        else
        {
            user.LastLoginAt = now;
            if (!string.IsNullOrWhiteSpace(displayName)) user.DisplayName = displayName!;
        }

        _db.IdentityProviderLinks.Add(new IdentityProviderLink
        {
            UserId = user.Id,
            ProviderId = provider.Id,
            Subject = githubSubject,
            LinkedAt = now,
        });
        _db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "auth.login_success.github",
            ActorUserId = user.Id,
            AtUtc = now,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    private async Task<IdentityProvider> EnsureProviderAsync(CancellationToken ct)
    {
        var existing = await _db.IdentityProviders
            .FirstOrDefaultAsync(p => p.Kind == "github", ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        var now = DateTimeOffset.UtcNow;
        var p = new IdentityProvider
        {
            Kind = "github",
            DisplayName = "GitHub",
            Issuer = "https://github.com",
            Audience = "tamp-beacon",
            ConfigJson = "{}",
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.IdentityProviders.Add(p);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return p;
    }
}
