using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Wires <see cref="KeyManagementOptions"/> from <see cref="AuthOptions"/>
/// at options-resolution time rather than service-registration time.
/// </summary>
/// <remarks>
/// The deferred shape matters because adopters / tests can layer config
/// providers after <c>AddBeaconAuth</c> registers services — eager
/// <c>config.Bind(authOptions)</c> calls would lock in stale defaults
/// (this was the bug that left
/// <see cref="AuthOptions.DataProtectionKeyDirectory"/> pinned to its
/// hard-coded fallback in <c>WebApplicationFactory</c> integration
/// tests). Configuring via <see cref="IConfigureOptions{TOptions}"/>
/// reads <see cref="IOptions{TOptions}"/> at the moment the data-
/// protection ring asks for them, by which point every provider has
/// landed.
/// </remarks>
internal sealed class KeyManagementOptionsConfigurator : IConfigureOptions<KeyManagementOptions>
{
    private readonly IOptions<AuthOptions> _auth;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<KeyManagementOptionsConfigurator> _log;

    public KeyManagementOptionsConfigurator(
        IOptions<AuthOptions> auth,
        ILoggerFactory loggerFactory,
        ILogger<KeyManagementOptionsConfigurator> log)
    {
        _auth = auth;
        _loggerFactory = loggerFactory;
        _log = log;
    }

    public void Configure(KeyManagementOptions options)
    {
        var auth = _auth.Value;

        var dir = auth.DataProtectionKeyDirectory;
        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "could not prepare data-protection key directory '{Dir}' — falling back to in-memory keys " +
                "(every pod restart silently invalidates session cookies); production deploys must ensure " +
                "the PVC mount is writable",
                dir);
        }

        if (Directory.Exists(dir))
        {
            options.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(dir), _loggerFactory);
        }

        switch (auth.KeyProtection.Mode)
        {
            case KeyProtectionMode.None:
                // Plaintext-on-disk. KeyProtectionWarningService emits the
                // operator-facing warning at startup; no encryptor wired here.
                break;

            case KeyProtectionMode.SecretFile:
                options.XmlEncryptor = new SecretFileXmlEncryptor(auth.KeyProtection.SecretFilePath!);
                break;

            case KeyProtectionMode.X509:
                var cert = LoadX509(auth.KeyProtection);
                // X509 protection delegates to the framework's built-in
                // CertificateXmlEncryptor (it's `internal sealed` so we
                // can't `new` it directly — instead we tap into the same
                // wiring path the public ProtectKeysWithCertificate
                // extension uses by setting XmlEncryptor to a thin
                // wrapper that round-trips through the framework type).
                // The simplest stable handle is `ProtectKeysWithCertificate`
                // applied to a throwaway IDataProtectionBuilder over a
                // capture-services collection — which is overkill. The
                // cleanest path: instantiate the framework's
                // CertificateXmlEncryptor via its public-ish constructor
                // (it accepts ILoggerFactory) through DI activator.
                options.XmlEncryptor = CreateCertificateXmlEncryptor(cert, _loggerFactory);
                break;

            default:
                throw new InvalidOperationException(
                    $"unknown KeyProtectionMode '{auth.KeyProtection.Mode}' — valid values: None, SecretFile, X509");
        }
    }

    private static X509Certificate2 LoadX509(KeyProtectionOptions kp) =>
        string.IsNullOrEmpty(kp.X509CertPassword)
            ? X509CertificateLoader.LoadPkcs12FromFile(kp.X509CertPath!, password: null)
            : X509CertificateLoader.LoadPkcs12FromFile(kp.X509CertPath!, kp.X509CertPassword);

    /// <summary>
    /// The framework's <c>CertificateXmlEncryptor</c> is <c>internal</c>, so
    /// instantiating it directly requires reflection. The type and
    /// constructor signature have been stable since .NET 6; the lookup is
    /// guarded so a future framework rename surfaces an actionable error
    /// rather than a NullReferenceException deep in the ring.
    /// </summary>
    private static IXmlEncryptor CreateCertificateXmlEncryptor(X509Certificate2 cert, ILoggerFactory loggerFactory)
    {
        var t = typeof(KeyManagementOptions).Assembly
            .GetType("Microsoft.AspNetCore.DataProtection.XmlEncryption.CertificateXmlEncryptor", throwOnError: false)
            ?? throw new InvalidOperationException(
                "Microsoft.AspNetCore.DataProtection.XmlEncryption.CertificateXmlEncryptor not found in the " +
                "data-protection assembly — the framework may have renamed the type. " +
                "Switch to KeyProtectionMode.SecretFile or pin the framework version that ships it.");
        var instance = Activator.CreateInstance(t, cert, loggerFactory)
            ?? throw new InvalidOperationException(
                "CertificateXmlEncryptor activator returned null — constructor signature may have changed.");
        return (IXmlEncryptor)instance;
    }
}
