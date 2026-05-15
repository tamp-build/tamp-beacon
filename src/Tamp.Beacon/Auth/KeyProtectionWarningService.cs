using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Surfaces a single startup-time warning when the data-protection key
/// ring is configured to run unencrypted on disk (TAM-219). The warning
/// is intentionally loud — adopters running OSS tamp-beacon on a shared
/// cluster shouldn't be able to silently roll a deploy that lets a
/// stolen PVC snapshot become session-forgery material.
/// </summary>
internal sealed class KeyProtectionWarningService : IHostedService
{
    private readonly ILogger<KeyProtectionWarningService> _log;
    private readonly KeyProtectionMode _mode;

    public KeyProtectionWarningService(IOptions<AuthOptions> options, ILogger<KeyProtectionWarningService> log)
    {
        _log = log;
        _mode = options.Value.KeyProtection.Mode;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_mode == KeyProtectionMode.None)
        {
            _log.LogWarning(
                "Beacon:Auth:KeyProtection:Mode is None — the data-protection key ring is being written to disk in PLAINTEXT. " +
                "Anyone with read access to the key directory can forge session cookies. This is acceptable for lab/inner-loop " +
                "deployments where the PVC is operator-controlled; production adopter deploys must set Mode to SecretFile or " +
                "X509. See docs/production-checklist.md for the setup steps.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
