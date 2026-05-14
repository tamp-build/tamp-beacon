using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Beacon;
using Tamp.Beacon.Auth;
using Xunit;

namespace Tamp.Beacon.Tests;

public sealed class ProjectTokensEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";
    public ProjectTokensEndpointTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Mint_Returns_Plaintext_Once()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"mint-{Guid.NewGuid():N}".Substring(0, 12);
        await admin.PostAsJsonAsync("/api/projects", new { slug, name = "mintp" });

        var mint = await admin.PostAsJsonAsync($"/api/projects/{slug}/tokens", new { label = "ci-runner-prod" });
        Assert.Equal(HttpStatusCode.Created, mint.StatusCode);
        var body = await mint.Content.ReadFromJsonAsync<JsonElement>();
        var plaintext = body.GetProperty("token").GetString();
        Assert.NotNull(plaintext);
        Assert.StartsWith("tbk_", plaintext!);
        Assert.True(plaintext!.Length > 30, $"token too short: {plaintext}");

        // List does NOT include plaintext.
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tokens");
        var tokens = list.GetProperty("tokens").EnumerateArray().ToList();
        Assert.Single(tokens);
        Assert.False(tokens[0].TryGetProperty("token", out _),
            "token plaintext leaked in list response");
    }

    [Fact]
    public async Task Mint_Rejects_Empty_Label_With_400()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"emp-{Guid.NewGuid():N}".Substring(0, 12);
        await admin.PostAsJsonAsync("/api/projects", new { slug, name = "emp" });

        var resp = await admin.PostAsJsonAsync($"/api/projects/{slug}/tokens", new { label = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Revoke_Marks_Token_And_Resolves_To_Null()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"rvk-{Guid.NewGuid():N}".Substring(0, 12);
        await admin.PostAsJsonAsync("/api/projects", new { slug, name = "rvk" });

        var mint = await admin.PostAsJsonAsync($"/api/projects/{slug}/tokens", new { label = "to-revoke" });
        var body = await mint.Content.ReadFromJsonAsync<JsonElement>();
        var tokenId = body.GetProperty("id").GetInt64();
        var plaintext = body.GetProperty("token").GetString()!;

        // Slice 4 will call ResolveAsync; verify it works pre-revoke.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ProjectTokenService>();
            var resolved = await svc.ResolveAsync(plaintext);
            Assert.NotNull(resolved);
            Assert.Equal(tokenId, resolved!.Id);
        }

        var del = await admin.DeleteAsync($"/api/projects/{slug}/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Post-revoke: list still shows the row with revoked_at; resolve returns null.
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/tokens");
        var t0 = list.GetProperty("tokens")[0];
        Assert.NotEqual(JsonValueKind.Null, t0.GetProperty("revoked_at").ValueKind);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<ProjectTokenService>();
            var resolved = await svc.ResolveAsync(plaintext);
            Assert.Null(resolved);
        }
    }

    [Fact]
    public async Task Mint_For_Unknown_Project_Returns_404()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var resp = await admin.PostAsJsonAsync("/api/projects/no-such-thing/tokens", new { label = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Token_Plaintext_Is_Cryptographically_Distinct_Across_Mints()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"dst-{Guid.NewGuid():N}".Substring(0, 12);
        await admin.PostAsJsonAsync("/api/projects", new { slug, name = "dst" });

        var first = await admin.PostAsJsonAsync($"/api/projects/{slug}/tokens", new { label = "a" });
        var second = await admin.PostAsJsonAsync($"/api/projects/{slug}/tokens", new { label = "b" });
        var fbody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var sbody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(fbody.GetProperty("token").GetString(), sbody.GetProperty("token").GetString());
    }
}
