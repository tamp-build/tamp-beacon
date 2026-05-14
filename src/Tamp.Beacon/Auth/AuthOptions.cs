using System;
using System.ComponentModel.DataAnnotations;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Bound from the <c>Beacon:Auth</c> config section. Holds the policy knobs
/// for the TAM-214 auth model: setup-token TTL, password-hash work factors,
/// and (in Slice 2) the GitHub OIDC client config.
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
}
