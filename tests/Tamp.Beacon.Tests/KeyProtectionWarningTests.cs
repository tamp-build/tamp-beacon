using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Auth;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// TAM-219 — the startup-time warning fires when the data-protection
/// key ring runs in plaintext mode. The warning is what saves an
/// adopter from silently rolling a production deploy that hands out
/// session-forgery material to anyone with PVC snapshot rights.
/// </summary>
public sealed class KeyProtectionWarningTests
{
    private sealed class RecordingLogger : ILogger<KeyProtectionWarningService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, System.Exception? exception, System.Func<TState, System.Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static KeyProtectionWarningService Build(KeyProtectionMode mode, out RecordingLogger logger)
    {
        logger = new RecordingLogger();
        var authOpts = new AuthOptions { KeyProtection = new KeyProtectionOptions { Mode = mode } };
        return new KeyProtectionWarningService(Options.Create(authOpts), logger);
    }

    [Fact]
    public async Task None_Mode_Emits_Warning_At_Startup()
    {
        var svc = Build(KeyProtectionMode.None, out var logger);
        await svc.StartAsync(CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("PLAINTEXT", warning.Message);
        Assert.Contains("SecretFile", warning.Message);
        Assert.Contains("X509", warning.Message);
        Assert.Contains("docs/production-checklist.md", warning.Message);
    }

    [Fact]
    public async Task SecretFile_Mode_Stays_Silent()
    {
        var svc = Build(KeyProtectionMode.SecretFile, out var logger);
        await svc.StartAsync(CancellationToken.None);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task X509_Mode_Stays_Silent()
    {
        var svc = Build(KeyProtectionMode.X509, out var logger);
        await svc.StartAsync(CancellationToken.None);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
