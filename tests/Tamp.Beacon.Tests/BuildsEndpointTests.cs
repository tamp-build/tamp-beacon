using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Read-side build surface. Each test seeds builds directly via the
/// fixture's <see cref="BeaconAppFixture.SeedBuildAsync"/> helper so the
/// list / detail / pagination contracts can be exercised without round-
/// tripping through the OTLP ingest pipeline.
/// </summary>
public sealed class BuildsEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";
    public BuildsEndpointTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task List_Returns_401_For_Anonymous()
    {
        var resp = await _fx.Client.GetAsync("/api/builds");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task List_Returns_Builds_For_Sysadmin_Across_Projects()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"lar-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "lar");
        await _fx.SeedBuildAsync(pid, projectName: slug);
        await _fx.SeedBuildAsync(pid, outcome: "failure", projectName: slug);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/builds?project={slug}");
        var builds = list.GetProperty("builds").EnumerateArray().ToList();
        Assert.Equal(2, builds.Count);
        Assert.All(builds, b => Assert.Equal(slug, b.GetProperty("project_slug").GetString()));
    }

    [Fact]
    public async Task List_Filters_By_Outcome()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"oc-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "oc");
        await _fx.SeedBuildAsync(pid, outcome: "success", projectName: slug);
        await _fx.SeedBuildAsync(pid, outcome: "failure", projectName: slug);
        await _fx.SeedBuildAsync(pid, outcome: "success", projectName: slug);

        var failed = await admin.GetFromJsonAsync<JsonElement>($"/api/builds?project={slug}&outcome=failure");
        var rows = failed.GetProperty("builds").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal("failure", rows[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task List_Rejects_Bad_Outcome_With_400()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var resp = await admin.GetAsync("/api/builds?outcome=meh");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task List_Delta_Polling_Honors_Since_Seq()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"sq-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "sq");
        await _fx.SeedBuildAsync(pid, projectName: slug);
        await _fx.SeedBuildAsync(pid, projectName: slug);
        await _fx.SeedBuildAsync(pid, projectName: slug);

        var first = await admin.GetFromJsonAsync<JsonElement>($"/api/builds?project={slug}");
        var maxSeq = first.GetProperty("next_seq").GetInt64();

        // since_seq = max → empty delta + next_seq unchanged
        var empty = await admin.GetFromJsonAsync<JsonElement>($"/api/builds?project={slug}&since_seq={maxSeq}");
        Assert.Empty(empty.GetProperty("builds").EnumerateArray());
        Assert.Equal(maxSeq, empty.GetProperty("next_seq").GetInt64());

        // since_seq = max - 1 → one delta build
        var delta = await admin.GetFromJsonAsync<JsonElement>($"/api/builds?project={slug}&since_seq={maxSeq - 1}");
        Assert.Single(delta.GetProperty("builds").EnumerateArray());
    }

    [Fact]
    public async Task List_Hides_Builds_From_NonMember()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("outsider-blt", "outsider-pw-12345");
        using var sysadmin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"hd-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(sysadmin, slug, "hd");
        await _fx.SeedBuildAsync(pid, projectName: slug);

        using var outsider = await _fx.LoginAsAsync("outsider-blt", "outsider-pw-12345");
        var list = await outsider.GetFromJsonAsync<JsonElement>($"/api/builds?project={slug}");
        Assert.Empty(list.GetProperty("builds").EnumerateArray());
    }

    [Fact]
    public async Task Detail_Returns_404_For_Unknown_Id()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var resp = await admin.GetAsync("/api/builds/99999999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Detail_Returns_Build_With_Targets_For_Member()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"dt-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "dt");
        var buildId = await _fx.SeedBuildAsync(pid,
            projectName: slug,
            successfulTargets: new[] { "Restore", "Compile" },
            failedTargets: new[] { "Test" });

        var resp = await admin.GetFromJsonAsync<JsonElement>($"/api/builds/{buildId}");
        Assert.Equal(slug, resp.GetProperty("build").GetProperty("project_slug").GetString());
        var targetNames = resp.GetProperty("targets").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("Restore", targetNames);
        Assert.Contains("Compile", targetNames);
        Assert.Contains("Test", targetNames);
    }

    [Fact]
    public async Task Detail_Returns_404_For_NonMember()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("ghost-detail", "ghost-pw-12345");
        using var sysadmin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"gd-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(sysadmin, slug, "gd");
        var buildId = await _fx.SeedBuildAsync(pid, projectName: slug);

        using var ghost = await _fx.LoginAsAsync("ghost-detail", "ghost-pw-12345");
        var resp = await ghost.GetAsync($"/api/builds/{buildId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ProjectScoped_List_Returns_Only_That_Projects_Builds()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slugA = $"pa-{Guid.NewGuid():N}".Substring(0, 12);
        var slugB = $"pb-{Guid.NewGuid():N}".Substring(0, 12);
        var pidA = await _fx.CreateProjectAsync(admin, slugA, "pa");
        var pidB = await _fx.CreateProjectAsync(admin, slugB, "pb");
        await _fx.SeedBuildAsync(pidA, projectName: slugA);
        await _fx.SeedBuildAsync(pidB, projectName: slugB);

        var listA = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slugA}/builds");
        var rowsA = listA.GetProperty("builds").EnumerateArray().ToList();
        Assert.Single(rowsA);
        Assert.Equal(slugA, rowsA[0].GetProperty("project_slug").GetString());
    }

    [Fact]
    public async Task ProjectScoped_List_Returns_404_For_NonMember()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("ghost-psl", "ghost-pw-12345");
        using var sysadmin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"px-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(sysadmin, slug, "px");

        using var ghost = await _fx.LoginAsAsync("ghost-psl", "ghost-pw-12345");
        var resp = await ghost.GetAsync($"/api/projects/{slug}/builds");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
