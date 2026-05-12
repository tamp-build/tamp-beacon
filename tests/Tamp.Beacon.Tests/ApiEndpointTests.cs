using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tamp.Beacon.Otlp;
using Tamp.Beacon.Sdk;
using Tamp.Beacon.Tests.Fixtures;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Spins up the host in-process via WebApplicationFactory and exercises the
/// API surface end-to-end. Each test gets its own temp directory for the
/// SQLite + VAPID files so they don't share state.
/// </summary>
public sealed class ApiEndpointTests : IClassFixture<ApiEndpointTests.BeaconFactory>, IDisposable
{
    private readonly BeaconFactory _factory;
    private readonly HttpClient _client;

    public ApiEndpointTests(BeaconFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Healthz_ReturnsOkAndVapidPublicKey()
    {
        var health = await _client.GetFromJsonAsync<HealthStatus>("/healthz");
        Assert.NotNull(health);
        Assert.Equal("ok", health!.Status);
        Assert.False(string.IsNullOrEmpty(health.VapidPublicKey));
        Assert.False(string.IsNullOrEmpty(health.DbPath));
    }

    [Fact]
    public async Task PostTraces_AcceptsTampPayload()
    {
        var req = OtlpFixtures.TrivialSuccess("ApiTestProject");
        var resp = await _client.PostAsJsonAsync("/v1/traces", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task PostTraces_RejectsNonTampPayload()
    {
        var req = OtlpFixtures.NonTampPayload();
        var resp = await _client.PostAsJsonAsync("/v1/traces", req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("tamp-beacon", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBuilds_ReturnsIngestedBuildAndCursors()
    {
        var project = $"BuildsListTest-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project));

        var list = await _client.GetFromJsonAsync<BuildList>($"/api/builds?project={project}");
        Assert.NotNull(list);
        Assert.Single(list!.Builds);
        Assert.Equal(project, list.Builds[0].ProjectName);
        Assert.True(list.NextSeq > 0);
    }

    [Fact]
    public async Task GetBuilds_FiltersByArea()
    {
        var project = $"AreaFilter-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project, "frontend"));
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project, "backend"));

        var fe = await _client.GetFromJsonAsync<BuildList>($"/api/builds?project={project}&area=frontend");
        Assert.NotNull(fe);
        Assert.Single(fe!.Builds);
        Assert.Equal("frontend", fe.Builds[0].ProjectArea);
    }

    [Fact]
    public async Task GetBuildById_ReturnsDetailWithTargetsAndCommands()
    {
        var project = $"DetailTest-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project));
        var list = await _client.GetFromJsonAsync<BuildList>($"/api/builds?project={project}");
        var id = list!.Builds[0].Id;

        var detail = await _client.GetFromJsonAsync<BuildDetail>($"/api/builds/{id}");
        Assert.NotNull(detail);
        Assert.Equal(project, detail!.Build.ProjectName);
        Assert.Equal(3, detail.Targets.Count);
        Assert.Equal(2, detail.Commands.Count);
        Assert.Single(detail.Events);
    }

    [Fact]
    public async Task GetBuildById_ReturnsNotFoundForUnknownId()
    {
        var resp = await _client.GetAsync("/api/builds/9999999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetProjects_GroupsByNameAndArea()
    {
        var project = $"ProjFacet-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project, "frontend"));
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project, "frontend"));
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project, "backend"));

        var list = await _client.GetFromJsonAsync<ProjectList>("/api/projects");
        Assert.NotNull(list);

        var mine = list!.Projects.Where(p => p.Name == project).ToList();
        Assert.Equal(2, mine.Count);
        var fe = mine.Single(p => p.Area == "frontend");
        Assert.Equal(2, fe.BuildsCount);
    }

    [Fact]
    public async Task SubscribePush_PersistsRow()
    {
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        var req = new PushSubscriptionRequest
        {
            Endpoint = endpoint,
            Keys = new PushKeys { P256dh = new string('p', 87), Auth = new string('a', 22) },
            ProjectFilter = "HoldFast",
        };
        var resp = await _client.PostAsJsonAsync("/api/push/subscribe", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Round-trip: re-subscribing with the same endpoint updates rather than duplicates.
        var resp2 = await _client.PostAsJsonAsync("/api/push/subscribe", req);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
    }

    [Fact]
    public async Task SubscribePush_RejectsMissingKeys()
    {
        var req = new PushSubscriptionRequest
        {
            Endpoint = "https://push.example.com/abc",
            Keys = new PushKeys { P256dh = "", Auth = "" },
        };
        var resp = await _client.PostAsJsonAsync("/api/push/subscribe", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetSlowestTargets_ReturnsStats()
    {
        var project = $"Slowest-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project));
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project));

        var resp = await _client.GetFromJsonAsync<TargetStatList>($"/api/targets/slowest?project={project}");
        Assert.NotNull(resp);
        Assert.NotEmpty(resp!.Targets);
        Assert.All(resp.Targets, s => Assert.True(s.Samples > 0));
    }

    [Fact]
    public async Task BeaconClient_SmokeTest_GetBuilds()
    {
        // Verify the published BeaconClient SDK works against the live host.
        var project = $"SdkClient-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project));

        var beacon = new BeaconClient(_client);
        var list = await beacon.GetBuildsAsync(project: project);
        Assert.NotEmpty(list.Builds);
        Assert.All(list.Builds, b => Assert.Equal(project, b.ProjectName));
    }

    [Fact]
    public async Task Pagination_SinceSeqAdvances()
    {
        var project = $"Cursor-{Guid.NewGuid():N}";
        for (int i = 0; i < 5; i++)
            await _client.PostAsJsonAsync("/v1/traces", OtlpFixtures.TrivialSuccess(project));

        var first = await _client.GetFromJsonAsync<BuildList>($"/api/builds?project={project}&limit=2");
        Assert.Equal(2, first!.Builds.Count);

        var second = await _client.GetFromJsonAsync<BuildList>($"/api/builds?project={project}&limit=2&since_seq={first.NextSeq}");
        Assert.Equal(2, second!.Builds.Count);
        Assert.True(second.Builds[0].Seq > first.NextSeq);
    }

    [Fact]
    public async Task PostMetrics_AcksTampPayload()
    {
        var payload = new ExportMetricsServiceRequest
        {
            ResourceMetrics =
            {
                new ResourceMetrics
                {
                    ScopeMetrics =
                    {
                        new ScopeMetrics { Scope = new InstrumentationScope { Name = "Tamp.Build", Version = "1.4.0" } },
                    },
                },
            },
        };
        var resp = await _client.PostAsJsonAsync("/v1/metrics", payload);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    public sealed class BeaconFactory : WebApplicationFactory<Program>
    {
        private string _tempDir = "";

        protected override IHost CreateHost(IHostBuilder builder)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "tamp-beacon-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);

            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string?>("Beacon:DbPath", Path.Combine(_tempDir, "test.sqlite")),
                    new KeyValuePair<string, string?>("Beacon:VapidKeyPath", Path.Combine(_tempDir, "vapid.key")),
                    new KeyValuePair<string, string?>("Beacon:VapidSubject", "mailto:tests@tamp.local"),
                    new KeyValuePair<string, string?>("Beacon:FailureWorkerIntervalMs", "60000"),
                });
            });

            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (!string.IsNullOrEmpty(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}

