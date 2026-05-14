using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamp.Beacon.Auth;

/// <summary>
/// First-run bootstrap printer. On startup, when the singleton
/// <see cref="Models.SetupState"/> row reports the cluster is not yet set up,
/// mints a one-time setup token, stores the SHA-256 hash + issuance time on
/// the row, persists the plaintext to disk (PVC-backed), and prints it to
/// stdout where <c>kubectl logs</c> can recover it. The trust boundary is
/// pod-log readership: anyone who can see container logs is treated as the
/// rightful operator.
/// </summary>
public sealed class SetupTokenManager : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SetupTokenManager> _log;
    private readonly AuthOptions _auth;
    private readonly BeaconOptions _beacon;

    public SetupTokenManager(
        IServiceProvider sp,
        ILogger<SetupTokenManager> log,
        IOptions<AuthOptions> auth,
        IOptions<BeaconOptions> beacon)
    {
        _sp = sp;
        _log = log;
        _auth = auth.Value;
        _beacon = beacon.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();

        var state = await db.SetupStateEntries.FirstOrDefaultAsync(s => s.Id == 1, cancellationToken)
            .ConfigureAwait(false);

        if (state is null)
        {
            state = new Models.SetupState { Id = 1, IsComplete = false };
            db.SetupStateEntries.Add(state);
        }

        if (state.IsComplete)
        {
            _log.LogInformation("setup already complete; bootstrap printer suppressed");
            // Belt-and-braces: if a stale token file is on disk after a successful setup, clear it.
            TryClearTokenFile();
            return;
        }

        var entropyBytes = RandomNumberGenerator.GetBytes(_auth.SetupTokenEntropyBytes);
        var token = ToBase64Url(entropyBytes);
        var tokenHash = Sha256Hex(token);

        state.PendingTokenHash = tokenHash;
        state.PendingTokenIssuedAt = DateTimeOffset.UtcNow;

        db.AuthAuditLog.Add(new Models.AuthAuditLogEntry
        {
            Event = "setup.token_minted",
            AtUtc = DateTimeOffset.UtcNow,
            DetailJson = $"{{\"ttl_seconds\":{_auth.SetupTokenTtlSeconds}}}",
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        TryWriteTokenFile(token);
        PrintBootstrapBanner(token);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>SHA-256(lower-hex) of the input — used to compare presented tokens against the persisted hash.</summary>
    public static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    private void TryWriteTokenFile(string token)
    {
        var path = _beacon.SetupTokenPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, token);
#pragma warning disable CA1416 // Unix-only chmod — file is only used in container deploys.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
#pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not persist setup token to {Path}; stdout copy remains the only record", path);
        }
    }

    private void TryClearTokenFile()
    {
        try
        {
            if (File.Exists(_beacon.SetupTokenPath)) File.Delete(_beacon.SetupTokenPath);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "could not delete consumed setup token file {Path}", _beacon.SetupTokenPath);
        }
    }

    private void PrintBootstrapBanner(string token)
    {
        var ttl = _auth.SetupTokenTtlSeconds.ToString(CultureInfo.InvariantCulture);
        var lines = new[]
        {
            "================================================================",
            " tamp-beacon — first-run setup token",
            "----------------------------------------------------------------",
            $" token:    {token}",
            $" ttl:      {ttl}s",
            " consume:  POST /setup with this token + new admin user/password",
            " recover:  restart the pod to mint a fresh token",
            "================================================================",
        };
        foreach (var line in lines) Console.WriteLine(line);
        _log.LogInformation("setup token printed to stdout (ttl_seconds={Ttl})", _auth.SetupTokenTtlSeconds);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
