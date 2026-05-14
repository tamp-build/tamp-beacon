using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Beacon;
using Tamp.Beacon.Auth;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Drives the admin recovery flow without going through the CLI binary —
/// invokes <see cref="PasswordResetService"/> directly to mint the token,
/// then verifies <c>POST /admin/recover</c> consumes it, replays are
/// rejected, and the new password unlocks <c>/break-glass</c>.
/// </summary>
public sealed class AdminRecoverFlowTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string OldPassword = "correct-horse-battery-staple";
    private const string NewPassword = "completely-different-pw-12";

    public AdminRecoverFlowTests(BeaconAppFixture fx) => _fx = fx;

    private async Task<string> MintResetTokenAsync(string username)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var reset = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
        var token = await reset.MintAsync(username);
        Assert.NotNull(token);
        return token!;
    }

    [Fact]
    public async Task MintAsync_Returns_Null_For_Unknown_User()
    {
        await _fx.EnsureAdminAsync(Admin, OldPassword);
        using var scope = _fx.Factory.Services.CreateScope();
        var reset = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
        var token = await reset.MintAsync("ghost-user-that-does-not-exist");
        Assert.Null(token);
    }

    [Fact]
    public async Task Recover_Rejects_Invalid_Token_With_401()
    {
        await _fx.EnsureAdminAsync(Admin, OldPassword);
        // Mint a real token so the user has a PendingResetHash row; present a different token.
        _ = await MintResetTokenAsync(Admin);

        var resp = await _fx.Client.PostAsJsonAsync("/admin/recover", new
        {
            username = Admin,
            token = "this-is-definitely-not-the-real-token",
            new_password = NewPassword,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Recover_Rejects_Short_Password_With_400()
    {
        await _fx.EnsureAdminAsync(Admin, OldPassword);
        var token = await MintResetTokenAsync(Admin);

        var resp = await _fx.Client.PostAsJsonAsync("/admin/recover", new
        {
            username = Admin,
            token,
            new_password = "tooshort",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Recover_Rejects_Empty_Body_With_400()
    {
        var resp = await _fx.Client.PostAsJsonAsync("/admin/recover", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}

/// <summary>
/// The destructive happy-path test for admin recovery — runs in its own
/// class so the password swap doesn't leak into other tests.
/// </summary>
public sealed class AdminRecoverHappyPathTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    private const string Admin = "scott";
    private const string OldPassword = "correct-horse-battery-staple";
    private const string NewPassword = "rotated-strong-password-9";

    public AdminRecoverHappyPathTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Recover_End_To_End_Replaces_Password_And_Rejects_Replay()
    {
        await _fx.EnsureAdminAsync(Admin, OldPassword);

        // 1. Mint a reset token directly via the service (CLI parity).
        string token;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var reset = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            token = (await reset.MintAsync(Admin))!;
            Assert.False(string.IsNullOrEmpty(token));
        }

        // 2. POST /admin/recover happy path.
        var consume = await _fx.Client.PostAsJsonAsync("/admin/recover", new
        {
            username = Admin,
            token,
            new_password = NewPassword,
        });
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);

        // 3. Replay must 401 (PendingResetHash cleared).
        var replay = await _fx.Client.PostAsJsonAsync("/admin/recover", new
        {
            username = Admin,
            token,
            new_password = "another-attempt-pw",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // 4. Old password must NOT log in.
        var oldLogin = await _fx.Client.PostAsJsonAsync("/break-glass", new { username = Admin, password = OldPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        // 5. New password DOES log in.
        var newLogin = await _fx.Client.PostAsJsonAsync("/break-glass", new { username = Admin, password = NewPassword });
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);

        // 6. DB-side: PendingResetHash + PendingResetIssuedAt cleared; audit log captured both events.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
            var u = await db.Users.SingleAsync(x => x.Username == Admin);
            Assert.Null(u.PendingResetHash);
            Assert.Null(u.PendingResetIssuedAt);

            var events = await db.AuthAuditLog.OrderBy(e => e.Id).Select(e => e.Event).ToListAsync();
            Assert.Contains("admin.password_reset_token_minted", events);
            Assert.Contains("admin.password_reset_consumed", events);
        }
    }
}
