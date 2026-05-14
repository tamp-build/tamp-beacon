using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Per-test-class fixture: spins up a fresh Postgres 17 container plus a
/// <see cref="WebApplicationFactory{TEntryPoint}"/> wrapped around the real
/// <c>Program.cs</c>. Each fixture instance gets its own container so test
/// classes that exercise the same endpoints don't fight over the
/// <c>setup_state</c> row.
/// <para>
/// The fixture also captures stdout via a redirected <see cref="StringWriter"/>
/// so tests can recover the setup-token banner printed at boot without
/// scraping the container logs.
/// </para>
/// </summary>
public sealed class BeaconAppFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private BeaconWebFactory? _factory;
    private string? _tokenPath;

    public WebApplicationFactory<Program> Factory => _factory
        ?? throw new InvalidOperationException("fixture not initialized — InitializeAsync must run first");

    public System.Net.Http.HttpClient Client => Factory.CreateClient();

    /// <summary>
    /// Returns the setup token persisted by <c>SetupTokenManager</c> at boot.
    /// The bootstrap printer writes the plaintext to the configured
    /// <c>Beacon:SetupTokenPath</c> file (PVC-backed in production; per-test
    /// temp file here) so we don't have to scrape global stdout — which
    /// would race across parallel fixtures sharing <c>Console.Out</c>.
    /// </summary>
    public string? ExtractBootstrapToken()
    {
        if (_tokenPath is null) return null;
        if (!File.Exists(_tokenPath)) return null;
        return File.ReadAllText(_tokenPath).Trim();
    }

    public async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("beacon_test")
            .WithUsername("beacon")
            .WithPassword("beacon")
            .Build();
        await _pg.StartAsync();

        _tokenPath = Path.Combine(Path.GetTempPath(), $"beacon-test-{Guid.NewGuid():N}.token");

        _factory = new BeaconWebFactory(_pg.GetConnectionString(), _tokenPath);
        // Trigger host build so the SetupTokenManager runs and the banner
        // is persisted to _tokenPath.
        _ = _factory.Services;
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_pg is not null) await _pg.DisposeAsync();
        if (_tokenPath is not null && File.Exists(_tokenPath))
        {
            try { File.Delete(_tokenPath); } catch { /* best-effort */ }
        }
    }

    private sealed class BeaconWebFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _tokenPath;
        public BeaconWebFactory(string connectionString, string tokenPath)
        {
            _connectionString = connectionString;
            _tokenPath = tokenPath;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Beacon:ConnectionString"] = _connectionString,
                    ["Beacon:SetupTokenPath"] = _tokenPath,
                });
            });
        }
    }
}
