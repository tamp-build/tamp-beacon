using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tamp.Beacon.Tests;

public sealed class ProjectMembersEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";
    public ProjectMembersEndpointTests(BeaconAppFixture fx) => _fx = fx;

    private async Task<string> CreateProjectAsync(System.Net.Http.HttpClient client, string namePrefix = "p")
    {
        var slug = $"{namePrefix}-{Guid.NewGuid():N}".Substring(0, Math.Min(15, namePrefix.Length + 9));
        await client.PostAsJsonAsync("/api/projects", new { slug, name = slug });
        return slug;
    }

    [Fact]
    public async Task Add_Member_Returns_201_And_Lists()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("alice", "alice-password-12");
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = await CreateProjectAsync(admin, "addm");

        var add = await admin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "alice", role = "viewer" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/members");
        var usernames = list.GetProperty("members").EnumerateArray()
            .Select(m => m.GetProperty("username").GetString())
            .ToList();
        Assert.Contains(Admin, usernames);
        Assert.Contains("alice", usernames);
    }

    [Fact]
    public async Task Add_Duplicate_Member_Returns_409()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("alice-dup", "dup-password-12");
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = await CreateProjectAsync(admin, "dupm");

        await admin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "alice-dup", role = "viewer" });
        var second = await admin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "alice-dup", role = "admin" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Add_Unknown_User_Returns_404()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = await CreateProjectAsync(admin, "ghst");

        var resp = await admin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "no-such-user", role = "viewer" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Add_Invalid_Role_Returns_400()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("alice-role", "role-password-12");
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = await CreateProjectAsync(admin, "role");

        var resp = await admin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "alice-role", role = "superuser" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Demote_Last_Admin_Returns_409()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        // For this test scott is project admin AND sysadmin. Need to drop
        // sysadmin so the RBAC check exercises project-level admin role,
        // not the sysadmin bypass. We do this by promoting another user
        // first then demoting scott — but that requires sysadmin too.
        //
        // Simpler: do the demote against an existing admin row directly.
        // The min-one-admin invariant fires before sysadmin-bypass — read
        // the source.

        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = await CreateProjectAsync(admin, "lone");
        var members = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/members");
        var scottMemberId = members.GetProperty("members")[0].GetProperty("id").GetInt64();

        var patch = await admin.PatchAsJsonAsync($"/api/projects/{slug}/members/{scottMemberId}", new { role = "viewer" });
        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
    }

    [Fact]
    public async Task Remove_Last_Admin_Returns_409()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = await CreateProjectAsync(admin, "rmla");
        var members = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/members");
        var scottMemberId = members.GetProperty("members")[0].GetProperty("id").GetInt64();

        var del = await admin.DeleteAsync($"/api/projects/{slug}/members/{scottMemberId}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);
    }

    [Fact]
    public async Task Demote_Works_When_Another_Admin_Exists()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("alice-coadmin", "coadmin-password-1");
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = await CreateProjectAsync(admin, "coadm");
        await admin.PostAsJsonAsync($"/api/projects/{slug}/members", new { username = "alice-coadmin", role = "admin" });

        var members = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/members");
        var scottId = members.GetProperty("members").EnumerateArray()
            .First(m => m.GetProperty("username").GetString() == Admin)
            .GetProperty("id").GetInt64();

        var patch = await admin.PatchAsJsonAsync($"/api/projects/{slug}/members/{scottId}", new { role = "viewer" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
    }
}
