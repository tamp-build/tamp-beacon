using System.ComponentModel.DataAnnotations;

namespace Tamp.Beacon.Auth;

/// <summary>
/// How the ASP.NET Core data-protection key ring is encrypted at rest.
/// Without protection, anyone with read access to the key directory can
/// forge any session cookie — a stolen PVC snapshot yields full session
/// forgery against the beacon, including the system admin (TAM-219).
/// </summary>
public enum KeyProtectionMode
{
    /// <summary>
    /// Keys are written to disk in plaintext. Lab/inner-loop default.
    /// Production deploys MUST switch to <see cref="SecretFile"/> or
    /// <see cref="X509"/> — a startup warning fires when this mode is
    /// active to surface that.
    /// </summary>
    None = 0,

    /// <summary>
    /// AES-256-GCM with a 32-byte symmetric key read from disk. Simpler
    /// than <see cref="X509"/> for adopters who don't already manage
    /// certificates — drop the 32 bytes into a Kubernetes Secret and
    /// mount it at <see cref="KeyProtectionOptions.SecretFilePath"/>.
    /// Rotation = replace the file (existing keys re-encrypt on next
    /// write; in-flight cookies remain valid until the old key file is
    /// removed and the ring rolls).
    /// </summary>
    SecretFile = 1,

    /// <summary>
    /// X.509 certificate (PFX). Framework-provided protection via
    /// <c>ProtectKeysWithCertificate</c> — common path for adopters who
    /// already run cert-manager / Vault-issued PFXs.
    /// </summary>
    X509 = 2,
}

/// <summary>
/// Configures how the data-protection key ring is protected at rest
/// (TAM-219). Bound from <c>Beacon:Auth:KeyProtection</c>.
/// </summary>
public sealed class KeyProtectionOptions : IValidatableObject
{
    /// <summary>
    /// Encryption mode for the on-disk key ring.
    /// </summary>
    public KeyProtectionMode Mode { get; set; } = KeyProtectionMode.None;

    /// <summary>
    /// Path to the 32-byte AES-256 key file. Required when
    /// <see cref="Mode"/> is <see cref="KeyProtectionMode.SecretFile"/>.
    /// The file must contain exactly 32 raw bytes (the operator is free
    /// to generate it however: <c>openssl rand 32 &gt; key</c>,
    /// <c>head -c 32 /dev/urandom</c>, or a CSPRNG of their choosing).
    /// </summary>
    public string? SecretFilePath { get; set; }

    /// <summary>
    /// Path to the X.509 PFX file. Required when <see cref="Mode"/> is
    /// <see cref="KeyProtectionMode.X509"/>. The file must contain the
    /// private key (encryption uses the public key, decryption needs
    /// the private key).
    /// </summary>
    public string? X509CertPath { get; set; }

    /// <summary>
    /// Password for the PFX file. Optional — leave unset if the PFX has
    /// no password.
    /// </summary>
    public string? X509CertPassword { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        switch (Mode)
        {
            case KeyProtectionMode.SecretFile:
                if (string.IsNullOrWhiteSpace(SecretFilePath))
                {
                    yield return new ValidationResult(
                        "Beacon:Auth:KeyProtection:SecretFilePath is required when Mode=SecretFile",
                        new[] { nameof(SecretFilePath) });
                }
                break;
            case KeyProtectionMode.X509:
                if (string.IsNullOrWhiteSpace(X509CertPath))
                {
                    yield return new ValidationResult(
                        "Beacon:Auth:KeyProtection:X509CertPath is required when Mode=X509",
                        new[] { nameof(X509CertPath) });
                }
                break;
        }
    }
}
