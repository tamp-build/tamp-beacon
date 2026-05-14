using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Beacon;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Validates the slowest + flakiest rollups over a small hand-crafted
/// dataset. Builds are seeded directly so the rollup math is observable
/// against known inputs.
/// </summary>
public sealed class TargetRollupTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";
    public TargetRollupTests(BeaconAppFixture fx) => _fx = fx;

    private async Task SeedTargetWithDurationAsync(long buildId, string name, long durationNs, string status = "success")
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        db.Targets.Add(new Tamp.Beacon.Models.Target
        {
            BuildId = buildId, Name = name, Status = status,
            StartedUnixNs = 1_700_000_000_000_000_000L,
            DurationNs = durationNs, RawTags = "{}",
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Slowest_Ranks_Targets_By_Avg_Duration()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"sl-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "sl");

        var b1 = await _fx.SeedBuildAsync(pid, projectName: slug);
        var b2 = await _fx.SeedBuildAsync(pid, projectName: slug);

        await SeedTargetWithDurationAsync(b1, "Restore", 1_000_000_000);   // 1s
        await SeedTargetWithDurationAsync(b2, "Restore", 1_500_000_000);   // 1.5s
        await SeedTargetWithDurationAsync(b1, "Compile", 5_000_000_000);   // 5s
        await SeedTargetWithDurationAsync(b2, "Compile", 7_000_000_000);   // 7s
        await SeedTargetWithDurationAsync(b1, "Test",    2_000_000_000);   // 2s

        var resp = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/targets/slowest");
        var rows = resp.GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Compile", rows[0].GetProperty("name").GetString());  // highest avg
        Assert.Equal("Test", rows[1].GetProperty("name").GetString());
        Assert.Equal("Restore", rows[2].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Slowest_Returns_404_For_NonMember()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("ghost-slow", "ghost-pw-12345");
        using var sysadmin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"sl-priv-{Guid.NewGuid():N}".Substring(0, 14);
        await _fx.CreateProjectAsync(sysadmin, slug, "p");

        using var ghost = await _fx.LoginAsAsync("ghost-slow", "ghost-pw-12345");
        var resp = await ghost.GetAsync($"/api/projects/{slug}/targets/slowest");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Flakiest_Computes_FailRate_And_Hides_Below_Min_Samples()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"fl-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "fl");

        var b1 = await _fx.SeedBuildAsync(pid, projectName: slug);
        var b2 = await _fx.SeedBuildAsync(pid, projectName: slug);
        var b3 = await _fx.SeedBuildAsync(pid, projectName: slug);
        var b4 = await _fx.SeedBuildAsync(pid, projectName: slug);

        // ToleranceTest fails 2 / 4 runs → 50% fail rate, 4 samples → above min(3)
        await SeedTargetWithDurationAsync(b1, "ToleranceTest", 1_000_000_000, "success");
        await SeedTargetWithDurationAsync(b2, "ToleranceTest", 1_000_000_000, "failure");
        await SeedTargetWithDurationAsync(b3, "ToleranceTest", 1_000_000_000, "success");
        await SeedTargetWithDurationAsync(b4, "ToleranceTest", 1_000_000_000, "failure");

        // SteadyJob succeeds 4/4 → not flaky, excluded
        await SeedTargetWithDurationAsync(b1, "SteadyJob", 1_000_000_000, "success");
        await SeedTargetWithDurationAsync(b2, "SteadyJob", 1_000_000_000, "success");
        await SeedTargetWithDurationAsync(b3, "SteadyJob", 1_000_000_000, "success");
        await SeedTargetWithDurationAsync(b4, "SteadyJob", 1_000_000_000, "success");

        // BarelyTried fails 1/2 → below default min_samples(3), excluded
        await SeedTargetWithDurationAsync(b1, "BarelyTried", 1_000_000_000, "success");
        await SeedTargetWithDurationAsync(b2, "BarelyTried", 1_000_000_000, "failure");

        var resp = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/targets/flakiest");
        var rows = resp.GetProperty("targets").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal("ToleranceTest", rows[0].GetProperty("name").GetString());
        Assert.Equal(0.5, rows[0].GetProperty("fail_rate").GetDouble(), precision: 3);
        Assert.Equal(4, rows[0].GetProperty("samples").GetInt32());
    }

    [Fact]
    public async Task Flakiest_Skipped_Status_Does_Not_Count_As_Failure()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"sk-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "sk");

        var b1 = await _fx.SeedBuildAsync(pid, projectName: slug);
        var b2 = await _fx.SeedBuildAsync(pid, projectName: slug);
        var b3 = await _fx.SeedBuildAsync(pid, projectName: slug);

        await SeedTargetWithDurationAsync(b1, "OptionalCheck", 1_000_000_000, "success");
        await SeedTargetWithDurationAsync(b2, "OptionalCheck", 1_000_000_000, "skipped");
        await SeedTargetWithDurationAsync(b3, "OptionalCheck", 1_000_000_000, "skipped");

        var resp = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/targets/flakiest");
        var rows = resp.GetProperty("targets").EnumerateArray().ToList();
        // Skipped runs aren't failures — fail_rate=0, so the target is hidden.
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Flakiest_Custom_Samples_Min_Lets_Smaller_Histories_Through()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"sm-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "sm");

        var b1 = await _fx.SeedBuildAsync(pid, projectName: slug);
        var b2 = await _fx.SeedBuildAsync(pid, projectName: slug);
        await SeedTargetWithDurationAsync(b1, "QuickCheck", 1_000_000_000, "failure");
        await SeedTargetWithDurationAsync(b2, "QuickCheck", 1_000_000_000, "success");

        // Default samples_min = 3, so default call hides this target.
        var def = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/targets/flakiest");
        Assert.Empty(def.GetProperty("targets").EnumerateArray());

        // Lowered floor → target appears.
        var lo = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/targets/flakiest?samples_min=2");
        Assert.Single(lo.GetProperty("targets").EnumerateArray());
    }
}
