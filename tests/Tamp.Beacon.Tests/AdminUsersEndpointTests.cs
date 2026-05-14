using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tamp.Beacon.Tests;

public sealed class AdminUsersEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Sysadmin = "scott";
    private const string SysadminPw = "correct-horse-battery-staple";
    public AdminUsersEndpointTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task List_Returns_Users_For_Sysadmin()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("li-alice", "li-password-1");
        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);
        var list = await sysadmin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var usernames = list.GetProperty("users").EnumerateArray()
            .Select(u => u.GetProperty("username").GetString())
            .ToHashSet();
        Assert.Contains(Sysadmin, usernames);
        Assert.Contains("li-alice", usernames);
    }

    [Fact]
    public async Task List_Rejects_NonSysadmin_With_403()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("non-sa", "non-sa-password-1");
        using var notSa = await _fx.LoginAsAsync("non-sa", "non-sa-password-1");
        var resp = await notSa.GetAsync("/api/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Promote_Demote_Roundtrip()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("promo-target", "promo-password-1");
        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);

        var promote = await sysadmin.PostAsync("/api/admin/users/promo-target/promote", content: null);
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);

        var demote = await sysadmin.PostAsync("/api/admin/users/promo-target/demote", content: null);
        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
    }

    [Fact]
    public async Task Demote_Last_Sysadmin_Returns_409()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);

        var resp = await sysadmin.PostAsync($"/api/admin/users/{Sysadmin}/demote", content: null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Promote_Unknown_User_Returns_404()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);
        var resp = await sysadmin.PostAsync("/api/admin/users/no-such-thing/promote", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Disable_Last_Sysadmin_Returns_409()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);
        var resp = await sysadmin.PostAsync($"/api/admin/users/{Sysadmin}/disable", content: null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Disable_Enable_Roundtrip_For_NonAdmin()
    {
        await _fx.EnsureAdminAsync(Sysadmin, SysadminPw);
        await _fx.SeedUserAsync("toggle-target", "toggle-password-1");
        using var sysadmin = await _fx.LoginAsAsync(Sysadmin, SysadminPw);

        var disable = await sysadmin.PostAsync("/api/admin/users/toggle-target/disable", content: null);
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        var enable = await sysadmin.PostAsync("/api/admin/users/toggle-target/enable", content: null);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
    }
}
