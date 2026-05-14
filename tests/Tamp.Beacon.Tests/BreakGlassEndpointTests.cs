using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Cookie-session contract for the local-admin login path. Each test in
/// the class shares the fixture's admin (created on first call via the
/// idempotent <c>EnsureAdminAsync</c> helper); rejection-path tests
/// don't disturb that state.
/// </summary>
public sealed class BreakGlassEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string AdminUser = "scott";
    private const string AdminPassword = "correct-horse-battery-staple";

    public BreakGlassEndpointTests(BeaconAppFixture fx) => _fx = fx;

    private async Task EnsureAdmin() => await _fx.EnsureAdminAsync(AdminUser, AdminPassword);

    [Fact]
    public async Task Rejects_Empty_Body_With_400()
    {
        await EnsureAdmin();
        var resp = await _fx.Client.PostAsJsonAsync("/break-glass", new { username = "", password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_Unknown_User_With_401()
    {
        await EnsureAdmin();
        var resp = await _fx.Client.PostAsJsonAsync("/break-glass", new { username = "ghost", password = "anything" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_Wrong_Password_With_401()
    {
        await EnsureAdmin();
        var resp = await _fx.Client.PostAsJsonAsync("/break-glass", new { username = AdminUser, password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Happy_Path_Returns_Session_And_Sets_Cookie()
    {
        await EnsureAdmin();
        using var client = _fx.CreateCookieClient();
        var resp = await client.PostAsJsonAsync("/break-glass", new { username = AdminUser, password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Contains("Set-Cookie", resp.Headers.ToString());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AdminUser, body.GetProperty("username").GetString());
        Assert.True(body.GetProperty("is_system_admin").GetBoolean());
    }

    [Fact]
    public async Task Me_Returns_401_Without_Cookie()
    {
        var resp = await _fx.Client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Me_Returns_Session_With_Cookie()
    {
        await EnsureAdmin();
        using var client = _fx.CreateCookieClient();
        var login = await client.PostAsJsonAsync("/break-glass", new { username = AdminUser, password = AdminPassword });
        login.EnsureSuccessStatusCode();

        var me = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AdminUser, body.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Logout_Clears_Cookie_Then_Me_401s()
    {
        await EnsureAdmin();
        using var client = _fx.CreateCookieClient();
        var login = await client.PostAsJsonAsync("/break-glass", new { username = AdminUser, password = AdminPassword });
        login.EnsureSuccessStatusCode();

        var preLogoutMe = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.OK, preLogoutMe.StatusCode);

        var logout = await client.PostAsync("/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var postLogoutMe = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, postLogoutMe.StatusCode);
    }
}

/// <summary>
/// Rate-limit contract for /break-glass. Held in its own fixture so the
/// rate-limited state doesn't bleed into <see cref="BreakGlassEndpointTests"/>.
/// </summary>
public sealed class BreakGlassRateLimitTests : IClassFixture<BreakGlassRateLimitFixture>
{
    private readonly BeaconAppFixture _fx;
    public BreakGlassRateLimitTests(BreakGlassRateLimitFixture fx) => _fx = fx;

    [Fact]
    public async Task Bucket_Drains_To_429_After_Configured_Limit()
    {
        // Fixture configures bucket size = 3, refill very slow.
        // First 3 attempts return 401 (or 400 for empty body); 4th onward returns 429.
        var got401 = 0;
        var got429 = 0;
        for (var i = 0; i < 10; i++)
        {
            var resp = await _fx.Client.PostAsJsonAsync("/break-glass", new { username = "ghost", password = "no" });
            if ((int)resp.StatusCode == 429) got429++;
            else if (resp.StatusCode == HttpStatusCode.Unauthorized) got401++;
        }
        Assert.True(got401 <= 3, $"more than 3 401s observed before rate limit kicked in (saw {got401})");
        Assert.True(got429 >= 1, $"expected at least 1 429 response (saw {got429})");
    }
}

/// <summary>
/// Specialized fixture that overrides the break-glass rate-limit bucket
/// size to 3 so the contract test fires quickly.
/// </summary>
public sealed class BreakGlassRateLimitFixture : BeaconAppFixture
{
    public BreakGlassRateLimitFixture()
    {
        ConfigOverrides["Beacon:Auth:BreakGlassFailureBucketSize"] = "3";
        ConfigOverrides["Beacon:Auth:BreakGlassRefillSeconds"] = "600";
    }
}
