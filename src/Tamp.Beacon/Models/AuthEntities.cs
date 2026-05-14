using System;
using System.Collections.Generic;

namespace Tamp.Beacon.Models;

/// <summary>
/// Singleton row (id=1) tracking whether first-run setup is complete and
/// whether a pending setup-token is outstanding. Probed by <c>/readyz</c>
/// and by the bootstrap printer on every process start.
/// </summary>
public sealed class SetupState
{
    public int Id { get; set; } = 1;

    /// <summary><c>true</c> once <c>POST /setup</c> has minted the first admin.</summary>
    public bool IsComplete { get; set; }

    /// <summary>SHA-256 of the in-flight setup token. Null when complete.</summary>
    public string? PendingTokenHash { get; set; }

    /// <summary>UTC time the pending token was minted; reset on each restart-before-claim.</summary>
    public DateTimeOffset? PendingTokenIssuedAt { get; set; }

    /// <summary>UTC time setup was completed (first admin created).</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// A human or service account principal. Local admins (created via the
/// break-glass setup-token flow) carry a populated <see cref="PasswordHash"/>;
/// federated users (added in Slice 2 via GitHub OIDC) have no local
/// password and authenticate via the <see cref="IdentityProvider"/> link.
/// </summary>
public sealed class User
{
    public long Id { get; set; }

    /// <summary>Login identifier — case-folded to lowercase on insert. Unique.</summary>
    public string Username { get; set; } = "";

    /// <summary>Display name; defaults to <see cref="Username"/>.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>argon2id digest (Konscious encoding); null for federated-only users.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>System Admin sees every project; cannot be revoked from the last sysadmin.</summary>
    public bool IsSystemAdmin { get; set; }

    /// <summary>Disabled accounts retain history but cannot authenticate.</summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<ProjectMember> ProjectMemberships { get; set; } = new List<ProjectMember>();
    public ICollection<IdentityProviderLink> IdentityProviderLinks { get; set; } = new List<IdentityProviderLink>();
}

/// <summary>
/// An authorization-scoped project. Slice 1 creates the table; project CRUD
/// endpoints + the FK from <see cref="Build"/> arrive in Slice 4.
/// </summary>
public sealed class Project
{
    public long Id { get; set; }

    /// <summary>URL-safe slug — unique. Lowercase a-z 0-9 hyphen.</summary>
    public string Slug { get; set; } = "";

    /// <summary>Human display name.</summary>
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public long CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when an admin soft-deletes the project; rows are retained for audit.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public ICollection<ProjectMember> Members { get; set; } = new List<ProjectMember>();
    public ICollection<ProjectToken> Tokens { get; set; } = new List<ProjectToken>();
}

/// <summary>SonarQube-style two-tier RBAC on a project.</summary>
public enum ProjectRole
{
    /// <summary>Read-only access to dashboards.</summary>
    Viewer = 0,
    /// <summary>Manage membership + ingest tokens + delete the project.</summary>
    Admin = 1,
}

/// <summary>
/// Join row between <see cref="User"/> and <see cref="Project"/>.
/// Min-one-admin invariant: deleting the last <see cref="ProjectRole.Admin"/>
/// row is rejected at the service layer (Slice 3).
/// </summary>
public sealed class ProjectMember
{
    public long Id { get; set; }

    public long ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public long UserId { get; set; }
    public User User { get; set; } = null!;

    public ProjectRole Role { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// Bearer token issued for a project's ingest endpoints. Plaintext shown
/// once at mint time, then never again — <see cref="TokenHash"/> is the
/// only persisted form. Revocable; rotatable.
/// </summary>
public sealed class ProjectToken
{
    public long Id { get; set; }

    public long ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>SHA-256 of the bearer token. Unique across the table.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>Friendly label shown in the admin UI ("ci-prod-runner-1").</summary>
    public string Label { get; set; } = "";

    public long CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Set when an admin revokes; non-null = unusable.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Identity-provider registry. Slice 1 creates the table empty; Slice 2's
/// GitHub OIDC wiring inserts the canonical "github" row. Multiple rows
/// supported for future GitLab / generic-OIDC integrations.
/// </summary>
public sealed class IdentityProvider
{
    public long Id { get; set; }

    /// <summary>Stable identifier — "github", "gitlab", etc. Unique.</summary>
    public string Kind { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>OIDC issuer URL.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>Required <c>aud</c> claim on incoming tokens.</summary>
    public string Audience { get; set; } = "";

    /// <summary>JSON config blob — provider-specific (allowed orgs, claim mappings, etc).</summary>
    public string ConfigJson { get; set; } = "{}";

    public bool IsEnabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Per-user link to an <see cref="IdentityProvider"/>. Lets a federated
/// login resolve to a stable internal user, and lets one human attach
/// multiple OIDC providers if their org migrates IDP later.
/// </summary>
public sealed class IdentityProviderLink
{
    public long Id { get; set; }

    public long UserId { get; set; }
    public User User { get; set; } = null!;

    public long ProviderId { get; set; }
    public IdentityProvider Provider { get; set; } = null!;

    /// <summary>Subject claim (<c>sub</c>) on the IDP's token. Unique per provider.</summary>
    public string Subject { get; set; } = "";

    public DateTimeOffset LinkedAt { get; set; }
}

/// <summary>
/// Append-only audit log for security-relevant events: setup-token mint /
/// consume, login success / failure, admin recovery, role changes, token
/// mint / revoke. Slice 1 writes setup + login events; later slices extend
/// the verb set.
/// </summary>
public sealed class AuthAuditLogEntry
{
    public long Id { get; set; }

    /// <summary>Stable verb — <c>setup.token_minted</c>, <c>auth.login_success</c>, etc.</summary>
    public string Event { get; set; } = "";

    /// <summary>Null when no user has yet been identified (e.g. failed login).</summary>
    public long? ActorUserId { get; set; }
    public User? ActorUser { get; set; }

    /// <summary>Optional structured detail (JSON).</summary>
    public string? DetailJson { get; set; }

    /// <summary>Origin IP captured at the request boundary.</summary>
    public string? RemoteIp { get; set; }

    public DateTimeOffset AtUtc { get; set; }
}
