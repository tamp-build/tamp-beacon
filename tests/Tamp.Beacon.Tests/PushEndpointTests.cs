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

public sealed class PushEndpointTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string AdminPw = "correct-horse-battery-staple";
    public PushEndpointTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Vapid_Public_Key_Is_Publicly_Fetchable()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        var resp = await _fx.Client.GetAsync("/api/push/vapid-public-key");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var key = doc.GetProperty("public_key").GetString();
        Assert.False(string.IsNullOrWhiteSpace(key));
        // Web Push P-256 public key is 65 bytes raw → 87 chars base64url
        // (Konscious/WebPush libraries emit either trimmed or padded; we
        // just check it's non-trivial).
        Assert.True(key!.Length > 60, $"VAPID key looks too short: {key}");
    }

    [Fact]
    public async Task Subscribe_Requires_Membership_404_For_NonMember()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        await _fx.SeedUserAsync("ghost-push", "ghost-pw-12345");
        using var sysadmin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"ps-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(sysadmin, slug, "ps");

        using var ghost = await _fx.LoginAsAsync("ghost-push", "ghost-pw-12345");
        var resp = await ghost.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe", new
        {
            endpoint = "https://push.example.com/sub/123",
            keys = new { p256dh = "p", auth = "a" },
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Subscribe_Rejects_Empty_Body_With_400()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"eb-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, slug, "eb");

        var resp = await admin.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe",
            new { endpoint = "", keys = new { p256dh = "", auth = "" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Subscribe_Persists_And_Lists_For_Member()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"sb-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, slug, "sb");
        var endpoint = $"https://push.example.com/sub/{Guid.NewGuid():N}";

        var first = await admin.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe", new
        {
            endpoint,
            keys = new { p256dh = "BPxxxx", auth = "AAaa" },
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Same endpoint → upsert (updated=true).
        var second = await admin.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe", new
        {
            endpoint,
            keys = new { p256dh = "BPyyyy", auth = "BBbb" },
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("updated").GetBoolean());

        // List shows exactly one row.
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/push");
        var subs = list.GetProperty("subscriptions").EnumerateArray().ToList();
        Assert.Single(subs);
        Assert.Equal(endpoint, subs[0].GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task Unsubscribe_Removes_Row_And_Replay_404s()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"un-{Guid.NewGuid():N}".Substring(0, 12);
        await _fx.CreateProjectAsync(admin, slug, "un");
        var endpoint = $"https://push.example.com/sub/{Guid.NewGuid():N}";

        await admin.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe", new
        {
            endpoint,
            keys = new { p256dh = "BPzzzz", auth = "CCcc" },
        });

        var del = await admin.PostAsJsonAsync($"/api/projects/{slug}/push/unsubscribe", new { endpoint });
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var replay = await admin.PostAsJsonAsync($"/api/projects/{slug}/push/unsubscribe", new { endpoint });
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
    }

    [Fact]
    public async Task Subscription_Cascades_When_Project_Archived()
    {
        await _fx.EnsureAdminAsync(Admin, AdminPw);
        using var admin = await _fx.LoginAsAsync(Admin, AdminPw);
        var slug = $"ca-{Guid.NewGuid():N}".Substring(0, 12);
        var pid = await _fx.CreateProjectAsync(admin, slug, "ca");

        var endpoint = $"https://push.example.com/sub/{Guid.NewGuid():N}";
        await admin.PostAsJsonAsync($"/api/projects/{slug}/push/subscribe", new
        {
            endpoint,
            keys = new { p256dh = "BPx", auth = "AA" },
        });

        // Hard-delete the project row to exercise the cascade — DELETE
        // /api/projects/{slug} only archives, which leaves subscriptions
        // attached. The cascade rule fires on actual row removal (e.g. a
        // future cleanup job).
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var p = await db.Projects.SingleAsync(x => x.Id == pid);
            db.Projects.Remove(p);
            await db.SaveChangesAsync();
        }

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var remaining = await db.PushSubscriptions.AnyAsync(s => s.Endpoint == endpoint);
            Assert.False(remaining, "subscription should have been cascade-deleted with the project");
        }
    }
}
