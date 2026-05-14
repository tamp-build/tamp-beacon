using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Seed a user directly via DI (skips the setup-token path). Useful
    /// when tests need multiple users with known credentials — sysadmin,
    /// viewer, non-member, etc.
    /// </summary>
    public async Task SeedUserAsync(string username, string password, bool isSystemAdmin = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Tamp.Beacon.Auth.PasswordHasher>();
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (existing is not null) return;
        db.Users.Add(new Tamp.Beacon.Models.User
        {
            Username = username,
            DisplayName = username,
            PasswordHash = hasher.Hash(password),
            IsSystemAdmin = isSystemAdmin,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Logs in via /break-glass and returns a cookie-handling HttpClient
    /// whose subsequent requests carry the session cookie. Tests that
    /// switch between identities use multiple of these.
    /// </summary>
    public async Task<System.Net.Http.HttpClient> LoginAsAsync(string username, string password)
    {
        var client = CreateCookieClient();
        var resp = await client.PostAsJsonAsync("/break-glass", new { username, password });
        resp.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>
    /// Seed a Build row directly via EF for tests that want full control over
    /// outcome / targets / status (the OTLP-ingest path is exercised in
    /// <c>OtlpEndpointTests</c>; here we bypass it so build-shape concerns
    /// don't leak into read-side test setup).
    /// </summary>
    public async Task<long> SeedBuildAsync(
        long projectId,
        string outcome = "success",
        long startedUnixNs = 1_700_000_000_000_000_000L,
        long durationNs = 100_000_000_000L,
        string? projectName = null,
        string[]? successfulTargets = null,
        string[]? failedTargets = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var maxSeq = await db.Builds.MaxAsync(b => (long?)b.Seq) ?? 0;
        var build = new Tamp.Beacon.Models.Build
        {
            Seq = maxSeq + 1,
            ProjectId = projectId,
            ProjectName = projectName ?? "seeded",
            Organization = projectName ?? "seeded",
            StartedUnixNs = startedUnixNs,
            DurationNs = durationNs,
            ExitCode = outcome == "success" ? 0 : 1,
            Outcome = outcome,
            TargetsTotal = (successfulTargets?.Length ?? 0) + (failedTargets?.Length ?? 0),
            TargetsFailed = failedTargets?.Length ?? 0,
            RawTags = "{}",
        };
        db.Builds.Add(build);
        await db.SaveChangesAsync();

        foreach (var n in successfulTargets ?? System.Array.Empty<string>())
            db.Targets.Add(new Tamp.Beacon.Models.Target
            {
                BuildId = build.Id, Name = n, Status = "success",
                StartedUnixNs = startedUnixNs, DurationNs = durationNs, RawTags = "{}",
            });
        foreach (var n in failedTargets ?? System.Array.Empty<string>())
            db.Targets.Add(new Tamp.Beacon.Models.Target
            {
                BuildId = build.Id, Name = n, Status = "failure",
                StartedUnixNs = startedUnixNs, DurationNs = durationNs, RawTags = "{}",
            });
        await db.SaveChangesAsync();
        return build.Id;
    }

    /// <summary>
    /// Spin up a project owned by the given admin client (uses /api/projects).
    /// Returns the project_id resolved from the DB so subsequent SeedBuildAsync
    /// calls can attach builds.
    /// </summary>
    public async Task<long> CreateProjectAsync(System.Net.Http.HttpClient adminClient, string slug, string name)
    {
        var resp = await adminClient.PostAsJsonAsync("/api/projects", new { slug, name });
        resp.EnsureSuccessStatusCode();
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        return await db.Projects.Where(p => p.Slug == slug).Select(p => p.Id).SingleAsync();
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
