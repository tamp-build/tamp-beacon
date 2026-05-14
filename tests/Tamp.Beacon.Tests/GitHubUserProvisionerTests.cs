using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tamp.Beacon;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Models;
using Xunit;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Direct tests for <see cref="GitHubUserProvisioner"/>. Bypasses the
/// OAuth dance — the OAuth handler in production calls this with parsed
/// GitHub claims, so testing the provisioner alone covers the auth
/// model's user-side surface without standing up a fake OAuth server.
/// </summary>
public sealed class GitHubUserProvisionerTests : IClassFixture<BeaconAppFixture>
{
    private readonly BeaconAppFixture _fx;
    public GitHubUserProvisionerTests(BeaconAppFixture fx) => _fx = fx;

    private (IServiceScope Scope, BeaconDbContext Db, GitHubUserProvisioner P) CreateScope()
    {
        var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        var p = scope.ServiceProvider.GetRequiredService<GitHubUserProvisioner>();
        return (scope, db, p);
    }

    [Fact]
    public async Task First_SignIn_Creates_User_With_NoPrivileges_And_Github_Link()
    {
        var subject = $"gh-{System.Guid.NewGuid():N}";
        var login = $"new-user-{subject[..8]}";

        var (scope, db, p) = CreateScope();
        using (scope)
        {
            var user = await p.EnsureUserAsync(login, subject, displayName: "New User");

            Assert.Equal(login, user.Username);
            Assert.Equal("New User", user.DisplayName);
            Assert.False(user.IsSystemAdmin);
            Assert.False(user.IsDisabled);
            Assert.Null(user.PasswordHash);

            // Identity-provider row was created on first call.
            var provider = await db.IdentityProviders.SingleAsync(x => x.Kind == "github");
            Assert.True(provider.IsEnabled);

            // The user→provider link was wired.
            var link = await db.IdentityProviderLinks.SingleAsync(l => l.UserId == user.Id);
            Assert.Equal(subject, link.Subject);
            Assert.Equal(provider.Id, link.ProviderId);
        }
    }

    [Fact]
    public async Task Repeat_SignIn_Same_Subject_Updates_LastLogin_And_DisplayName()
    {
        var subject = $"gh-{System.Guid.NewGuid():N}";
        var login = $"repeat-user-{subject[..8]}";

        var (scope, db, p) = CreateScope();
        using (scope)
        {
            var first = await p.EnsureUserAsync(login, subject, displayName: "Repeat User");
            var firstLogin = first.LastLoginAt;
            Assert.NotNull(firstLogin);

            await Task.Delay(50);
            var second = await p.EnsureUserAsync(login, subject, displayName: "Repeat User Renamed");
            Assert.Equal(first.Id, second.Id);
            Assert.Equal("Repeat User Renamed", second.DisplayName);
            Assert.True(second.LastLoginAt > firstLogin, "LastLoginAt should advance on repeat sign-in");

            // Single link, not duplicated on repeat.
            var linkCount = await db.IdentityProviderLinks.CountAsync(l => l.UserId == second.Id);
            Assert.Equal(1, linkCount);
        }
    }

    [Fact]
    public async Task SignIn_With_Existing_Local_Username_Attaches_Link_To_That_User()
    {
        // Seed a local user (think: setup-token admin "scott") then attempt
        // a GitHub sign-in with the same login. The provisioner should
        // attach the link to the existing row, not create a new "scott-2".
        var localUsername = $"local-{System.Guid.NewGuid():N}".Substring(0, 12);
        var ghSubject = $"gh-{System.Guid.NewGuid():N}";

        var (scope, db, p) = CreateScope();
        using (scope)
        {
            var localUser = new User
            {
                Username = localUsername,
                DisplayName = localUsername,
                PasswordHash = "$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                IsSystemAdmin = true,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            db.Users.Add(localUser);
            await db.SaveChangesAsync();

            var result = await p.EnsureUserAsync(localUsername, ghSubject, displayName: "Scott from GitHub");
            Assert.Equal(localUser.Id, result.Id);  // SAME user row
            Assert.True(result.IsSystemAdmin);       // privileges preserved
            Assert.NotNull(result.PasswordHash);     // local pw still works

            var link = await db.IdentityProviderLinks.SingleAsync(l => l.UserId == localUser.Id);
            Assert.Equal(ghSubject, link.Subject);
        }
    }

    [Fact]
    public async Task SignIn_Lowercases_Github_Login_For_Username_Match()
    {
        // GitHub stores login as the user typed it ("BrewingCoder").
        // We persist usernames in lowercase so SignIn with "BrewingCoder"
        // attaches to a local user named "brewingcoder".
        var localUsername = "brewingcoder-test";
        var ghSubject = $"gh-{System.Guid.NewGuid():N}";

        var (scope, db, p) = CreateScope();
        using (scope)
        {
            var local = new User
            {
                Username = localUsername,
                DisplayName = "Brewing Coder",
                PasswordHash = "$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                IsSystemAdmin = false,
                CreatedAt = System.DateTimeOffset.UtcNow,
            };
            db.Users.Add(local);
            await db.SaveChangesAsync();

            var result = await p.EnsureUserAsync(githubLogin: "BREWINGCODER-TEST", githubSubject: ghSubject, displayName: null);
            Assert.Equal(local.Id, result.Id);
        }
    }

    [Theory]
    [InlineData("", "subject")]
    [InlineData("   ", "subject")]
    [InlineData("login", "")]
    [InlineData("login", "   ")]
    public async Task EnsureUser_Rejects_Empty_Input(string login, string subject)
    {
        var (scope, _, p) = CreateScope();
        using (scope)
        {
            await Assert.ThrowsAsync<System.ArgumentException>(() =>
                p.EnsureUserAsync(login, subject, displayName: null));
        }
    }
}
