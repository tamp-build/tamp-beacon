using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Bound from the <c>Beacon:Auth</c> config section. Holds the policy knobs
/// for the TAM-214 auth model: setup-token TTL, password-hash work factors,
/// cookie session shape, GitHub OAuth client config, and password-recovery
/// TTL.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// How long a printed setup token is valid before <c>POST /setup</c> must
    /// be invoked. Restarting the process before consumption mints a fresh
    /// token (see TAM-214 threat model — restart is the recovery handle when
    /// the operator loses the original stdout copy).
    /// </summary>
    [Range(60, 24 * 60 * 60)]
    public int SetupTokenTtlSeconds { get; set; } = 60 * 60;

    /// <summary>
    /// Setup-token entropy. 32 bytes = 256 bits, base64url-encoded =
    /// 43-character token printed at boot.
    /// </summary>
    [Range(16, 128)]
    public int SetupTokenEntropyBytes { get; set; } = 32;

    /// <summary>
    /// argon2id memory cost (KiB). 64 MiB is the OWASP 2023 floor for
    /// interactive logins; tunable upward as hardware budget allows.
    /// </summary>
    [Range(8 * 1024, 1024 * 1024)]
    public int Argon2MemoryKib { get; set; } = 64 * 1024;

    /// <summary>argon2id iterations. OWASP 2023 floor = 3.</summary>
    [Range(1, 10)]
    public int Argon2Iterations { get; set; } = 3;

    /// <summary>argon2id parallelism. Single-threaded keeps web-tier CPU bursty rather than sustained.</summary>
    [Range(1, 16)]
    public int Argon2Parallelism { get; set; } = 1;

    /// <summary>
    /// Sliding session window for the cookie auth scheme. The cookie is
    /// re-issued on every authenticated request, so an active session
    /// stays alive indefinitely; an idle session expires after this many
    /// seconds.
    /// </summary>
    [Range(60, 30 * 24 * 60 * 60)]
    public int CookieIdleTtlSeconds { get; set; } = 7 * 24 * 60 * 60;

    /// <summary>
    /// Directory the data-protection ring writes its key files to. Must
    /// be on a persistent volume (the PVC) — losing the ring on pod
    /// restart silently invalidates every active session cookie.
    /// </summary>
    public string DataProtectionKeyDirectory { get; set; } = "/var/lib/tamp-beacon/data-protection-keys";

    /// <summary>
    /// Per-username allowlist for password recovery / reset operations.
    /// TTL window in which a freshly-minted reset token can be consumed.
    /// </summary>
    [Range(60, 24 * 60 * 60)]
    public int PasswordResetTokenTtlSeconds { get; set; } = 60 * 60;

    /// <summary>
    /// Number of failed <c>POST /break-glass</c> attempts (per client IP)
    /// before requests start returning 429. The bucket refills at the same
    /// rate (one slot per <see cref="BreakGlassRefillSeconds"/> seconds).
    /// </summary>
    [Range(1, 10_000)]
    public int BreakGlassFailureBucketSize { get; set; } = 10;

    /// <summary>How fast the per-IP break-glass failure bucket drains.</summary>
    [Range(1, 600)]
    public int BreakGlassRefillSeconds { get; set; } = 60;

    /// <summary>GitHub OAuth sign-in config. Optional — when disabled the only login path is /break-glass.</summary>
    public GitHubOAuthOptions GitHub { get; set; } = new();
}

/// <summary>
/// GitHub OAuth client config. Disabled when <see cref="ClientId"/> or
/// <see cref="ClientSecret"/> is unset; the OAuth handler is not registered
/// in that case and <c>/signin/github</c> returns 404.
/// </summary>
public sealed class GitHubOAuthOptions
{
    /// <summary>OAuth app client id.</summary>
    public string? ClientId { get; set; }

    /// <summary>OAuth app client secret.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Authorize endpoint. Overridable for tests.</summary>
    public string AuthorizationEndpoint { get; set; } = "https://github.com/login/oauth/authorize";

    /// <summary>Token endpoint. Overridable for tests.</summary>
    public string TokenEndpoint { get; set; } = "https://github.com/login/oauth/access_token";

    /// <summary>User info endpoint. Overridable for tests.</summary>
    public string UserInformationEndpoint { get; set; } = "https://api.github.com/user";

    /// <summary>Orgs membership endpoint. Overridable for tests.</summary>
    public string UserOrgsEndpoint { get; set; } = "https://api.github.com/user/orgs";

    /// <summary>
    /// Allowed GitHub logins (case-insensitive). Empty when no individual
    /// allowlist is configured — combined with <see cref="AllowedOrgs"/> via
    /// OR semantics: in either list = allowed.
    /// </summary>
    public List<string> AllowedLogins { get; set; } = new();

    /// <summary>
    /// Allowed GitHub orgs (case-insensitive). User must be a member of at
    /// least one for sign-in to succeed.
    /// </summary>
    public List<string> AllowedOrgs { get; set; } = new();

    /// <summary>True when the OAuth handler should be registered.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
