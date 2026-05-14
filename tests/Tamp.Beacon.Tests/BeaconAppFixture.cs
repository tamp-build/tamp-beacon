using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
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
public class BeaconAppFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _pg;
    private BeaconWebFactory? _factory;
    private string? _tokenPath;
    private string? _dpKeyDir;

    /// <summary>
    /// Subclasses can add or override config keys before the host builds.
    /// Populated keys win over the base fixture's defaults.
    /// </summary>
    protected Dictionary<string, string?> ConfigOverrides { get; } = new();

    public WebApplicationFactory<Program> Factory => _factory
        ?? throw new InvalidOperationException("fixture not initialized — InitializeAsync must run first");

    public System.Net.Http.HttpClient Client => Factory.CreateClient();

    /// <summary>
    /// Connection string the fixture's Postgres container is reachable at.
    /// Tests that need to seed the DB out-of-band (e.g. mint a reset token
    /// before the endpoint test runs) can spin up their own
    /// <see cref="BeaconDbContext"/> against this.
    /// </summary>
    public string? ConnectionString => _pg?.GetConnectionString();

    /// <summary>
    /// HttpClient that mirrors browser cookie semantics — break-glass +
    /// /me + logout tests share one of these so the session cookie
    /// persists across calls.
    /// </summary>
    public System.Net.Http.HttpClient CreateCookieClient()
    {
        return Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false,
        });
    }

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

    /// <summary>
    /// Idempotent helper for tests that need an authenticated session as
    /// a starting point. On first call, consumes the bootstrap token to
    /// mint an admin with the given creds. Subsequent calls are no-ops.
    /// </summary>
    public async Task EnsureAdminAsync(string username, string password)
    {
        var status = await Client.GetFromJsonAsync<JsonElement>("/setup/status");
        if (status.GetProperty("is_complete").GetBoolean()) return;

        var token = ExtractBootstrapToken()
            ?? throw new InvalidOperationException("setup token unavailable — fixture init may not have completed");

        var resp = await Client.PostAsJsonAsync("/setup", new
        {
            token,
            username,
            password,
            display_name = username,
        });
        resp.EnsureSuccessStatusCode();
    }

    public virtual async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("beacon_test")
            .WithUsername("beacon")
            .WithPassword("beacon")
            .Build();
        await _pg.StartAsync();

        _tokenPath = Path.Combine(Path.GetTempPath(), $"beacon-test-{Guid.NewGuid():N}.token");
        _dpKeyDir = Path.Combine(Path.GetTempPath(), $"beacon-test-dp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dpKeyDir);

        var config = new Dictionary<string, string?>
        {
            ["Beacon:ConnectionString"] = _pg.GetConnectionString(),
            ["Beacon:SetupTokenPath"] = _tokenPath,
            ["Beacon:Auth:DataProtectionKeyDirectory"] = _dpKeyDir,
            ["Beacon:Auth:BreakGlassFailureBucketSize"] = "1000",
        };
        foreach (var kv in ConfigOverrides) config[kv.Key] = kv.Value;

        _factory = new BeaconWebFactory(config);
        // Trigger host build so the SetupTokenManager runs and the banner
        // is persisted to _tokenPath.
        _ = _factory.Services;
    }

    public virtual async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_pg is not null) await _pg.DisposeAsync();
        if (_tokenPath is not null && File.Exists(_tokenPath))
        {
            try { File.Delete(_tokenPath); } catch { /* best-effort */ }
        }
        if (_dpKeyDir is not null && Directory.Exists(_dpKeyDir))
        {
            try { Directory.Delete(_dpKeyDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class BeaconWebFactory : WebApplicationFactory<Program>
    {
        private readonly Dictionary<string, string?> _config;
        public BeaconWebFactory(Dictionary<string, string?> config) => _config = config;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(_config);
            });
        }
    }
}
