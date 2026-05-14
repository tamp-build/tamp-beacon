using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Beacon;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// BuildConfig CRUD + aggregate stats (TAM-215). Builds are seeded
/// directly via the fixture; the OTLP-driven auto-create path is
/// covered in <see cref="OtlpEndpointTests"/>.
/// </summary>
public sealed class BuildConfigsEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";

    public BuildConfigsEndpointTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task List_Returns_Empty_For_New_Project()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"emp-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, slug, "emp");

        var resp = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/configs");
        Assert.Empty(resp.GetProperty("configs").EnumerateArray());
    }

    [Fact]
    public async Task Create_Rejects_Bad_Slug_With_400()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"bs-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, slug, "bs");

        var resp = await admin.PostAsJsonAsync($"/api/projects/{slug}/configs",
            new { slug = "BAD SLUG!", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Then_Get_Roundtrip()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"rt-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, pslug, "rt");

        var create = await admin.PostAsJsonAsync($"/api/projects/{pslug}/configs",
            new { slug = "main-ci", name = "Main CI", description = "PR + main builds" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var get = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{pslug}/configs/main-ci");
        Assert.Equal("main-ci", get.GetProperty("slug").GetString());
        Assert.Equal("Main CI", get.GetProperty("name").GetString());
        Assert.Equal("PR + main builds", get.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Create_Duplicate_Slug_Returns_409()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"dup-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, pslug, "dup");

        await admin.PostAsJsonAsync($"/api/projects/{pslug}/configs", new { slug = "first", name = "First" });
        var second = await admin.PostAsJsonAsync($"/api/projects/{pslug}/configs",
            new { slug = "first", name = "Also" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Update_Renames_Config()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"rn-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, pslug, "rn");
        await admin.PostAsJsonAsync($"/api/projects/{pslug}/configs", new { slug = "old", name = "Old" });

        var patch = await admin.PatchAsJsonAsync($"/api/projects/{pslug}/configs/old",
            new { name = "New", description = "renamed" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var body = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New", body.GetProperty("name").GetString());
        Assert.Equal("renamed", body.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Archive_Hides_Config_From_List_And_Get_404s()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"ar-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, pslug, "ar");
        await admin.PostAsJsonAsync($"/api/projects/{pslug}/configs", new { slug = "stale", name = "Stale" });

        var del = await admin.DeleteAsync($"/api/projects/{pslug}/configs/stale");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await admin.GetAsync($"/api/projects/{pslug}/configs/stale");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{pslug}/configs");
        Assert.Empty(list.GetProperty("configs").EnumerateArray());
    }

    [Fact]
    public async Task List_Returns_Aggregate_Stats_For_Seeded_Builds()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"st-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, pslug, "st");

        // Two configs, each with two builds. SeedBuildAsync's new
        // configSlug param wires the BuildConfig FK so the aggregate
        // queries have something to roll up.
        await _fx.SeedBuildAsync(pid, projectName: pslug, configSlug: "main-ci",
            successfulTargets: new[] { "Restore", "Compile" });
        await _fx.SeedBuildAsync(pid, outcome: "failure", projectName: pslug, configSlug: "main-ci",
            failedTargets: new[] { "Test" });
        await _fx.SeedBuildAsync(pid, projectName: pslug, configSlug: "nightly",
            successfulTargets: new[] { "Restore" });

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{pslug}/configs");
        var configs = list.GetProperty("configs").EnumerateArray().ToList();
        Assert.Equal(2, configs.Count);

        var main = configs.First(c => c.GetProperty("slug").GetString() == "main-ci");
        Assert.Equal(2, main.GetProperty("total_builds").GetInt32());
        // Last build is the failure (highest seq).
        Assert.Equal("failure", main.GetProperty("last_build").GetProperty("outcome").GetString());

        var nightly = configs.First(c => c.GetProperty("slug").GetString() == "nightly");
        Assert.Equal(1, nightly.GetProperty("total_builds").GetInt32());
        Assert.Equal("success", nightly.GetProperty("last_build").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task NonMember_Sees_404_On_Config_Route()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("ghost-cfg", "ghost-pw-12345");
        using var sysadmin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"hd-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(sysadmin, pslug, "hd");

        using var ghost = await _fx.LoginAsAsync("ghost-cfg", "ghost-pw-12345");
        var resp = await ghost.GetAsync($"/api/projects/{pslug}/configs");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

/// <summary>
/// Verifies the OTLP receiver auto-creates a BuildConfig from the
/// <c>tamp.build.config.name</c> tag and lands the Build under it.
/// </summary>
public sealed class OtlpBuildConfigAutoCreateTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";

    public OtlpBuildConfigAutoCreateTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Ingest_With_Config_Tag_Creates_Named_Config()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"ac-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, pslug, "ac");
        var tokenResp = await admin.PostAsJsonAsync($"/api/projects/{pslug}/tokens", new { label = "ci" });
        var ingestToken = (await tokenResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        // Use the proto fixture to mint a Tamp.Build span carrying a custom
        // config-name tag.
        var req = OtlpFixtures.TrivialBuildSuccess(project: pslug);
        req.ResourceSpans[0].ScopeSpans[0].Spans[0].Attributes.Add(
            new OpenTelemetry.Proto.Common.V1.KeyValue
            {
                Key = "tamp.build.config.name",
                Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "main-ci" },
            });
        var bytes = OtlpFixtures.ToProto(req);

        using var client = _fx.Factory.CreateClient();
        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new ByteArrayContent(bytes),
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingestToken);
        var ingest = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{pslug}/configs");
        var configs = list.GetProperty("configs").EnumerateArray()
            .Select(c => c.GetProperty("slug").GetString())
            .ToList();
        Assert.Contains("main-ci", configs);
    }

    [Fact]
    public async Task Ingest_Without_Config_Tag_Lands_Under_Default()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"df-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, pslug, "df");
        var tokenResp = await admin.PostAsJsonAsync($"/api/projects/{pslug}/tokens", new { label = "ci" });
        var ingestToken = (await tokenResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var req = OtlpFixtures.TrivialBuildSuccess(project: pslug);
        var bytes = OtlpFixtures.ToProto(req);

        using var client = _fx.Factory.CreateClient();
        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
        {
            Content = new ByteArrayContent(bytes),
        };
        msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingestToken);
        var ingest = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{pslug}/configs");
        var configs = list.GetProperty("configs").EnumerateArray()
            .Select(c => c.GetProperty("slug").GetString())
            .ToList();
        Assert.Contains("default", configs);
    }

    [Fact]
    public async Task Two_Ingests_Same_Config_Tag_Reuse_Single_Row()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var pslug = $"rr-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, pslug, "rr");
        var tokenResp = await admin.PostAsJsonAsync($"/api/projects/{pslug}/tokens", new { label = "ci" });
        var ingestToken = (await tokenResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        for (var i = 0; i < 2; i++)
        {
            var req = OtlpFixtures.TrivialBuildSuccess(
                project: pslug,
                traceIdHex: Guid.NewGuid().ToString("N"),
                spanIdHex: Guid.NewGuid().ToString("N").Substring(0, 16));
            req.ResourceSpans[0].ScopeSpans[0].Spans[0].Attributes.Add(
                new OpenTelemetry.Proto.Common.V1.KeyValue
                {
                    Key = "tamp.build.config.name",
                    Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "main-ci" },
                });
            using var client = _fx.Factory.CreateClient();
            var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/traces")
            {
                Content = new ByteArrayContent(OtlpFixtures.ToProto(req)),
            };
            msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ingestToken);
            (await client.SendAsync(msg)).EnsureSuccessStatusCode();
        }

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var project = await db.Projects.SingleAsync(p => p.Slug == pslug);
        var configCount = await db.BuildConfigs.CountAsync(c => c.ProjectId == project.Id);
        Assert.Equal(1, configCount);
        var buildCount = await db.Builds.CountAsync(b => b.ProjectId == project.Id);
        Assert.Equal(2, buildCount);
    }
}
