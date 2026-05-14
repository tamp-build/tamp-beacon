using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Project CRUD + visibility contract. Sysadmin creates a project + the
/// creator-becomes-project-admin invariant; viewer cannot mutate; non-
/// member gets 404 (existence hidden).
/// </summary>
public sealed class ProjectsEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";
    public ProjectsEndpointTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Create_Rejects_Invalid_Slug_With_400()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var client = await _fx.LoginAsAsync(Admin, AdminPw);
        var resp = await client.PostAsJsonAsync("/api/projects", new { slug = "Bad Slug!", name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("two-word-slug")]
    [InlineData("p123")]
    [InlineData("a-very-long-slug-that-still-fits-under-the-64-char-limit-easily")]
    public async Task Create_Accepts_Valid_Slug_Forms(string slug)
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var client = await _fx.LoginAsAsync(Admin, AdminPw);
        var resp = await client.PostAsJsonAsync("/api/projects", new { slug, name = slug });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Theory]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    [InlineData("UPPER")]
    [InlineData("with space")]
    [InlineData("with.dot")]
    [InlineData("a")]
    public async Task Create_Rejects_Malformed_Slug_Forms(string slug)
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var client = await _fx.LoginAsAsync(Admin, AdminPw);
        var resp = await client.PostAsJsonAsync("/api/projects", new { slug, name = "name" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Rejects_Anonymous_With_401()
    {
        var resp = await _fx.Client.PostAsJsonAsync("/api/projects", new { slug = "anon", name = "n" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Create_Conflicts_On_Duplicate_Slug_With_409()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var client = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"dup-{System.Guid.NewGuid():N}".Substring(0, 12);

        var a = await client.PostAsJsonAsync("/api/projects", new { slug, name = "first" });
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        var b = await client.PostAsJsonAsync("/api/projects", new { slug, name = "second" });
        Assert.Equal(HttpStatusCode.Conflict, b.StatusCode);
    }

    [Fact]
    public async Task Creator_Becomes_Project_Admin_Automatically()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var client = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"auto-{System.Guid.NewGuid():N}".Substring(0, 12);

        var created = await client.PostAsJsonAsync("/api/projects", new { slug, name = "AutoAdmin" });
        created.EnsureSuccessStatusCode();
        var detail = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", detail.GetProperty("my_role").GetString());
        Assert.Equal(1, detail.GetProperty("member_count").GetInt32());
    }

    [Fact]
    public async Task Update_Renames_Project_For_Admin()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var client = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"rn-{System.Guid.NewGuid():N}".Substring(0, 10);
        await client.PostAsJsonAsync("/api/projects", new { slug, name = "OldName" });

        var patch = await client.PatchAsJsonAsync($"/api/projects/{slug}", new { name = "NewName" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var detail = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NewName", detail.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Archive_Soft_Deletes_The_Project_And_Hides_From_List()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var client = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"arc-{System.Guid.NewGuid():N}".Substring(0, 10);
        await client.PostAsJsonAsync("/api/projects", new { slug, name = "to-archive" });

        var del = await client.DeleteAsync($"/api/projects/{slug}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // After archive: GET single returns 404, list excludes it.
        var get = await client.GetAsync($"/api/projects/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/projects");
        var slugs = list.GetProperty("projects").EnumerateArray()
            .Select(p => p.GetProperty("slug").GetString())
            .ToList();
        Assert.DoesNotContain(slug, slugs);
    }

    [Fact]
    public async Task NonMember_Sees_404_Not_403_On_Get()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var sysadminClient = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"priv-{System.Guid.NewGuid():N}".Substring(0, 10);
        await sysadminClient.PostAsJsonAsync("/api/projects", new { slug, name = "private" });

        // Demote scott to non-sysadmin so he becomes a vanilla user, then
        // sign in as a new user who isn't a member of the project.
        await _fx.SeedUserAsync("nonmember-test", "anonymouse-password-1");
        using var ghost = await _fx.LoginAsAsync("nonmember-test", "anonymouse-password-1");

        var resp = await ghost.GetAsync($"/api/projects/{slug}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
