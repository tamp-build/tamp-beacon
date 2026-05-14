using System.Collections.Generic;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Bound from the <c>Beacon:Auth</c> config section. Controls whether the OTLP
/// ingress path requires a verified GitHub Actions OIDC token, and the
/// repo_owner / repo → organization allowlist that maps verified tokens to
/// the organization scope they may submit telemetry as.
/// </summary>
public sealed class AuthOptions
{
    public const string DefaultIssuer = "https://token.actions.githubusercontent.com";

    /// <summary><c>Disabled</c> (default) skips auth entirely — paired with loopback-only binding
    /// this is the local-dev posture. <c>OidcGitHub</c> enforces a verified GitHub Actions JWT on
    /// every OTLP ingress request.</summary>
    public AuthMode Mode { get; set; } = AuthMode.Disabled;

    /// <summary>Required <c>aud</c> claim on incoming JWTs. Workflows request this audience when
    /// minting a token via <c>$ACTIONS_ID_TOKEN_REQUEST_URL?audience=&lt;value&gt;</c>.</summary>
    public string Audience { get; set; } = "tamp-beacon";

    /// <summary>OIDC issuer. Overridable for tests; production stays on the GitHub default.</summary>
    public string Issuer { get; set; } = DefaultIssuer;

    /// <summary>Allowlist of repo_owner / repo claims → organization. Order matters: the first
    /// matching entry wins. An incoming JWT whose <c>repository_owner</c>+<c>repository</c> do not
    /// match any entry is rejected with 403.</summary>
    public List<OrganizationMapping> Organizations { get; set; } = new();
}

public enum AuthMode
{
    Disabled,
    OidcGitHub,
}

/// <summary>
/// One row of the allowlist. <c>RepoOwner</c> must match the JWT's <c>repository_owner</c>
/// exactly (case-insensitive). <c>Repo</c> is optional — when null the mapping applies to
/// every repo under that owner; when set, the JWT's <c>repository</c> claim (which has the
/// shape <c>owner/name</c>) must match <c>owner/Repo</c> exactly.
/// </summary>
public sealed class OrganizationMapping
{
    public string RepoOwner { get; set; } = "";
    public string? Repo { get; set; }
    public string Organization { get; set; } = "";
}
