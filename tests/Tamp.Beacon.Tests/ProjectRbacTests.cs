using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Visibility + privilege boundary matrix: sysadmin sees all, project
/// member sees own project, viewer can read but not mutate, non-member
/// gets 404 (not 403) on all routes that touch a specific project.
/// </summary>
public sealed class ProjectRbacTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Sysadmin = "scott";
    private const string SysadminPw = "correct-horse-battery-staple";
    public ProjectRbacTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Sysadmin_Sees_All_Projects_Even_Without_Membership()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("creator-1", "creator-password-1");
        await _fx.SeedUserAsync("creator-2", "creator-password-2");

        using var c1 = await _fx.LoginAsAsync("creator-1", "creator-password-1");
        var slug1 = $"c1-{Guid.NewGuid():N}".Substring(0, 12);
        await c1.PostAsJsonAsync("/api/projects", new { slug = slug1, name = "c1-proj" });

        using var c2 = await _fx.LoginAsAsync("creator-2", "creator-password-2");
        var slug2 = $"c2-{Guid.NewGuid():N}".Substring(0, 12);
        await c2.PostAsJsonAsync("/api/projects", new { slug = slug2, name = "c2-proj" });

        // Sysadmin (scott) — not a member of either — should still see both.
        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);
        var list = await sysadmin.GetFromJsonAsync<JsonElement>("/api/projects");
        var slugs = list.GetProperty("projects").EnumerateArray()
            .Select(p => p.GetProperty("slug").GetString())
            .ToHashSet();
        Assert.Contains(slug1, slugs);
        Assert.Contains(slug2, slugs);
    }

    [Fact]
    public async Task NonMember_Sees_Empty_Project_List()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("ghost-list", "ghost-password-1");
        using var ghost = await _fx.LoginAsAsync("ghost-list", "ghost-password-1");

        var list = await ghost.GetFromJsonAsync<JsonElement>("/api/projects");
        Assert.Empty(list.GetProperty("projects").EnumerateArray());
    }

    [Fact]
    public async Task Viewer_Cannot_Mint_Tokens_403()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("viewer-bob", "viewer-password-1");

        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);
        var slug = $"vw-{Guid.NewGuid():N}".Substring(0, 12);
        await sysadmin.PostAsJsonAsync("/api/projects", new { slug, name = "vw" });
        await sysadmin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "viewer-bob", role = "viewer" });

        using var bob = await _fx.LoginAsAsync("viewer-bob", "viewer-password-1");
        var resp = await bob.PostAsJsonAsync($"/api/projects/{slug}/tokens", new { label = "should-fail" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_Can_Read_Members()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("viewer-carla", "viewer-password-2");

        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);
        var slug = $"vr-{Guid.NewGuid():N}".Substring(0, 12);
        await sysadmin.PostAsJsonAsync("/api/projects", new { slug, name = "vr" });
        await sysadmin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "viewer-carla", role = "viewer" });

        using var carla = await _fx.LoginAsAsync("viewer-carla", "viewer-password-2");
        var resp = await carla.GetAsync($"/api/projects/{slug}/members");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Viewer_Cannot_Add_Members_403()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("viewer-david", "viewer-password-3");
        await _fx.SeedUserAsync("intruder", "intruder-password-1");

        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);
        var slug = $"vp-{Guid.NewGuid():N}".Substring(0, 12);
        await sysadmin.PostAsJsonAsync("/api/projects", new { slug, name = "vp" });
        await sysadmin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "viewer-david", role = "viewer" });

        using var david = await _fx.LoginAsAsync("viewer-david", "viewer-password-3");
        var resp = await david.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "intruder", role = "viewer" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task NonMember_Gets_404_On_Token_List()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("outsider", "outsider-password-1");

        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);
        var slug = $"os-{Guid.NewGuid():N}".Substring(0, 12);
        await sysadmin.PostAsJsonAsync("/api/projects", new { slug, name = "os" });

        using var outsider = await _fx.LoginAsAsync("outsider", "outsider-password-1");
        var resp = await outsider.GetAsync($"/api/projects/{slug}/tokens");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
