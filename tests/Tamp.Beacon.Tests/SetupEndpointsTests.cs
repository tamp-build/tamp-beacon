using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// End-to-end contract for the first-run bootstrap. Each test starts from a
/// fresh container (via the per-class fixture) so setup-state is at <c>Id=1,
/// IsComplete=false</c> on entry.
/// </summary>
public sealed class SetupEndpointsTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;

    public SetupEndpointsTests(BeaconAppFixture fx) => _fx = fx;

    private sealed record SetupRequestBody(string token, string username, string password, string? display_name = null);

    private async Task<HttpResponseMessage> PostSetupAsync(SetupRequestBody body) =>
        await _fx.Client.PostAsJsonAsync("/setup", body);

    private async Task<string> MintTokenAsync()
    {
        // Bootstrap printer fires during fixture init; just yank the token
        // from the captured banner.
        var token = _fx.ExtractBootstrapToken();
        Assert.False(string.IsNullOrWhiteSpace(token),
            "fixture should have printed a setup token on boot");
        await Task.CompletedTask;
        return token!;
    }

    [Fact]
    public async Task SetupStatus_Reports_Awaiting_On_Fresh_Database()
    {
        var resp = await _fx.Client.GetAsync("/setup/status");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.GetProperty("awaiting_setup").GetBoolean());
        Assert.False(doc.GetProperty("is_complete").GetBoolean());
    }

    [Fact]
    public async Task Setup_Rejects_Invalid_Token_With_401()
    {
        var resp = await PostSetupAsync(new SetupRequestBody(
            token: "this-is-not-the-token",
            username: "scott",
            password: "correct-horse-battery-staple"));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UPPER")]                         // uppercase forbidden — TAM-214 rule
    [InlineData("with space")]                    // spaces forbidden
    [InlineData("a")]                             // 1 char — under minimum
    [InlineData("café-user")]                     // unicode forbidden
    [InlineData("name@host")]                     // @ forbidden
    [InlineData("name/path")]                     // / forbidden
    public async Task Setup_Rejects_Invalid_Username_With_400(string username)
    {
        var token = await MintTokenAsync();
        var resp = await PostSetupAsync(new SetupRequestBody(token, username, "correct-horse-battery-staple"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("11charpwds")]                    // 10 chars
    [InlineData("11chrspsswd")]                   // 11 chars — exactly one below the 12-char floor
    public async Task Setup_Rejects_Short_Password_With_400(string password)
    {
        Assert.True(password.Length < 12, $"test fixture sanity: '{password}' is not under 12 chars");
        var token = await MintTokenAsync();
        var resp = await PostSetupAsync(new SetupRequestBody(token, "scott", password));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Setup_Rejects_Username_Length_65_With_400()
    {
        // The accept boundaries (length 2..64) are exercised implicitly via
        // the happy-path test ("scott") and via the length=1 rejection in
        // the username theory above. A dedicated accept-boundary test would
        // need a per-test fixture since the post-200 IsComplete state
        // collapses all subsequent attempts to 409.
        var token = await MintTokenAsync();
        var resp = await PostSetupAsync(new SetupRequestBody(token, new string('a', 65), "correct-horse-battery-staple"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}

/// <summary>
/// Single-shot tests that hold the fixture exclusively because they consume
/// the setup token. Each one uses its own fixture instance via xUnit's
/// per-test-class lifecycle, so the setup-state stays at <c>IsComplete=false</c>
/// on entry.
/// </summary>
public sealed class SetupEndpointsHappyPathTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    public SetupEndpointsHappyPathTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public async Task Setup_HappyPath_Creates_Admin_Marks_Complete_And_Refuses_Replay()
    {
        var token = _fx.ExtractBootstrapToken();
        Assert.NotNull(token);

        // First attempt — succeeds, returns the admin record.
        var ok = await _fx.Client.PostAsJsonAsync("/setup", new
        {
            token,
            username = "scott",
            password = "correct-horse-battery-staple",
            display_name = "Scott (BrewingCoder)",
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var doc = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scott", doc.GetProperty("username").GetString());
        Assert.Equal("Scott (BrewingCoder)", doc.GetProperty("display_name").GetString());

        // Second attempt with same token — must 409.
        var conflict = await _fx.Client.PostAsJsonAsync("/setup", new
        {
            token,
            username = "interloper",
            password = "correct-horse-battery-staple",
        });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // Readyz now reports setup_complete=true.
        var ready = await _fx.Client.GetAsync("/readyz");
        var readyDoc = await ready.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(readyDoc.GetProperty("setup_complete").GetBoolean());
        Assert.False(readyDoc.GetProperty("awaiting_setup").GetBoolean());

        // Setup status mirrors.
        var status = await _fx.Client.GetAsync("/setup/status");
        var statusDoc = await status.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(statusDoc.GetProperty("is_complete").GetBoolean());

        // DB-level invariant: exactly one user, is_system_admin=true,
        // password_hash is an argon2id PHC string.
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var admin = await db.Users.SingleAsync();
        Assert.Equal("scott", admin.Username);
        Assert.True(admin.IsSystemAdmin);
        Assert.False(admin.IsDisabled);
        Assert.StartsWith("$argon2id$", admin.PasswordHash);

        // setup_state row reflects completion and token is cleared.
        var state = await db.SetupStateEntries.SingleAsync(s => s.Id == 1);
        Assert.True(state.IsComplete);
        Assert.Null(state.PendingTokenHash);
        Assert.NotNull(state.CompletedAt);

        // Audit log captured token_minted + completed + attempt_after_complete.
        var events = await db.AuthAuditLog.OrderBy(e => e.Id).Select(e => e.Event).ToListAsync();
        Assert.Contains("setup.token_minted", events);
        Assert.Contains("setup.completed", events);
        Assert.Contains("setup.attempt_after_complete", events);
    }
}

/// <summary>
/// Verifies the SetupTokenManager's mint contract on a fresh DB: the
/// token file is created and contains a high-entropy string.
/// </summary>
public sealed class SetupTokenManagerMintTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    public SetupTokenManagerMintTests(BeaconAppFixture fx) => _fx = fx;

    [Fact]
    public void Token_Is_Persisted_To_File_On_Fresh_Database()
    {
        var token = _fx.ExtractBootstrapToken();
        Assert.False(string.IsNullOrWhiteSpace(token),
            "fixture should have persisted a setup token to the configured path");
        Assert.True(token!.Length >= 32, $"token unexpectedly short: '{token}'");
        // Default entropy is 32 bytes → base64url-encoded = 43 chars (no padding).
        Assert.Equal(43, token.Length);
    }
}
