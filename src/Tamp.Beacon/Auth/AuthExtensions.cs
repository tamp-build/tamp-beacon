using System;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Auth;

public static class AuthExtensions
{
    /// <summary>Cookie auth scheme — holds the session principal.</summary>
    public const string CookieScheme = "BeaconCookie";

    /// <summary>OAuth handler scheme for the GitHub sign-in flow.</summary>
    public const string GitHubScheme = "GitHub";

    /// <summary>Claim type carrying the internal <see cref="User.Id"/>.</summary>
    public const string UserIdClaim = "beacon.user_id";

    /// <summary>Claim type stamped when <see cref="User.IsSystemAdmin"/> is true.</summary>
    public const string SystemAdminClaim = "beacon.system_admin";

    /// <summary>Claim type carrying <see cref="User.DisplayName"/>.</summary>
    public const string DisplayNameClaim = "beacon.display_name";

    public static IServiceCollection AddBeaconAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AuthOptions>()
            .Bind(config.GetSection("Beacon:Auth"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<PasswordHasher>();
        services.AddScoped<PasswordResetService>();
        services.AddScoped<GitHubUserProvisioner>();
        services.AddScoped<ProjectAuthorization>();
        services.AddScoped<ProjectTokenService>();
        services.AddScoped<Otlp.OtlpTraceReceiver>();
        services.AddSingleton<Otlp.OtlpMetricReceiver>();
        services.AddHostedService<SetupTokenManager>();

        AddDataProtection(services, config);
        AddAuthentication(services, config);
        services.AddAuthorization();

        // Per-IP rate limit on /break-glass to slow credential-stuffing.
        // Read options from DI at partition-creation time, NOT at policy
        // registration — config providers (especially WebApplicationFactory's
        // AddInMemoryCollection in tests) are applied AFTER service
        // registration runs, so an early bind would lock in stale defaults.
        services.AddRateLimiter(opts =>
        {
            opts.AddPolicy("break-glass", ctx =>
            {
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var auth = ctx.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
                return System.Threading.RateLimiting.RateLimitPartition.GetTokenBucketLimiter(
                    ip, _ => new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
                    {
                        TokenLimit = auth.BreakGlassFailureBucketSize,
                        TokensPerPeriod = 1,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(auth.BreakGlassRefillSeconds),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
            opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        return services;
    }

    private static void AddDataProtection(IServiceCollection services, IConfiguration config)
    {
        var auth = new AuthOptions();
        config.GetSection("Beacon:Auth").Bind(auth);
        try
        {
            if (!Directory.Exists(auth.DataProtectionKeyDirectory))
            {
                Directory.CreateDirectory(auth.DataProtectionKeyDirectory);
            }
        }
        catch (Exception ex)
        {
            // Fall through to in-memory keys if the configured dir is unwritable
            // (e.g. tests running without the PVC). A warning surfaces in logs;
            // production deploys must ensure the PVC mount is writable.
            Console.Error.WriteLine(
                $"[auth] could not prepare data-protection key directory '{auth.DataProtectionKeyDirectory}': {ex.Message}");
        }

        var dp = services.AddDataProtection().SetApplicationName("tamp-beacon");
        if (Directory.Exists(auth.DataProtectionKeyDirectory))
        {
            dp.PersistKeysToFileSystem(new DirectoryInfo(auth.DataProtectionKeyDirectory));
        }
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration config)
    {
        var auth = new AuthOptions();
        config.GetSection("Beacon:Auth").Bind(auth);

        var builder = services.AddAuthentication(o =>
        {
            o.DefaultAuthenticateScheme = CookieScheme;
            o.DefaultSignInScheme = CookieScheme;
            o.DefaultChallengeScheme = CookieScheme;
        });

        builder.AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = "tamp-beacon.sid";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromSeconds(auth.CookieIdleTtlSeconds);
            o.SlidingExpiration = true;
            // No login page — we return 401 JSON and the SPA shows its own UI.
            o.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            o.Events.OnValidatePrincipal = ValidateCookiePrincipalAsync;
        });

        if (auth.GitHub.IsConfigured)
        {
            builder.AddOAuth(GitHubScheme, o => ConfigureGitHubOAuth(o, auth.GitHub));
        }
    }

    private static void ConfigureGitHubOAuth(OAuthOptions o, GitHubOAuthOptions gh)
    {
        o.SignInScheme = CookieScheme;
        o.ClientId = gh.ClientId!;
        o.ClientSecret = gh.ClientSecret!;
        o.CallbackPath = "/signin/github/callback";
        o.AuthorizationEndpoint = gh.AuthorizationEndpoint;
        o.TokenEndpoint = gh.TokenEndpoint;
        o.UserInformationEndpoint = gh.UserInformationEndpoint;
        o.SaveTokens = false;
        // Scopes: user:email (basic profile + email) and read:org (so the
        // allowlist check can look up org memberships).
        o.Scope.Add("read:user");
        o.Scope.Add("user:email");
        o.Scope.Add("read:org");
        o.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        o.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
        o.ClaimActions.MapJsonKey("github.email", "email");
        o.ClaimActions.MapJsonKey("github.name", "name");
        o.Events.OnCreatingTicket = OnGitHubCreatingTicketAsync;
    }

    private static async Task OnGitHubCreatingTicketAsync(OAuthCreatingTicketContext ctx)
    {
        // Pull /user.
        var userReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, ctx.Options.UserInformationEndpoint);
        userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
        userReq.Headers.UserAgent.ParseAdd("tamp-beacon");
        var userResp = await ctx.Backchannel.SendAsync(userReq, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
        userResp.EnsureSuccessStatusCode();
        using var userDoc = JsonDocument.Parse(await userResp.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted).ConfigureAwait(false));
        ctx.RunClaimActions(userDoc.RootElement);

        var ghOpts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.GitHub;
        var login = userDoc.RootElement.GetProperty("login").GetString() ?? "";

        // Pull /user/orgs only when we have an org allowlist to honor.
        var memberOrgs = Array.Empty<string>();
        if (ghOpts.AllowedOrgs.Count > 0)
        {
            var orgsReq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, ghOpts.UserOrgsEndpoint);
            orgsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
            orgsReq.Headers.UserAgent.ParseAdd("tamp-beacon");
            var orgsResp = await ctx.Backchannel.SendAsync(orgsReq, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
            orgsResp.EnsureSuccessStatusCode();
            using var orgsDoc = JsonDocument.Parse(await orgsResp.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted).ConfigureAwait(false));
            memberOrgs = orgsDoc.RootElement.EnumerateArray()
                .Select(o => o.GetProperty("login").GetString() ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }

        // Allowlist evaluation: in either allowlist = allowed; both empty = nobody.
        var allowedByLogin = ghOpts.AllowedLogins.Any(a => string.Equals(a, login, StringComparison.OrdinalIgnoreCase));
        var allowedByOrg = ghOpts.AllowedOrgs.Any(a => memberOrgs.Any(m => string.Equals(m, a, StringComparison.OrdinalIgnoreCase)));
        if (!allowedByLogin && !allowedByOrg)
        {
            ctx.Fail("GitHub login is not in the tamp-beacon allowlist");
            return;
        }

        // Provision / refresh the User row, then rewrite the ticket identity
        // so the cookie principal carries the internal user_id, not the
        // GitHub-side numeric id.
        var provisioner = ctx.HttpContext.RequestServices.GetRequiredService<GitHubUserProvisioner>();
        var displayName = userDoc.RootElement.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;
        var ghIdLong = userDoc.RootElement.GetProperty("id").GetInt64();
        var user = await provisioner.EnsureUserAsync(
            githubLogin: login,
            githubSubject: ghIdLong.ToString(),
            displayName: displayName,
            ctx.HttpContext.RequestAborted).ConfigureAwait(false);

        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        // Strip claims that may identify the user as their GitHub id and
        // replace with internal claims so authorization policies bind to
        // our User table, not the IdP.
        var toRemove = identity.Claims
            .Where(c => c.Type == ClaimTypes.NameIdentifier || c.Type == ClaimTypes.Name)
            .ToList();
        foreach (var c in toRemove) identity.RemoveClaim(c);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Username));
        identity.AddClaim(new Claim(UserIdClaim, user.Id.ToString()));
        identity.AddClaim(new Claim(DisplayNameClaim, user.DisplayName));
        if (user.IsSystemAdmin) identity.AddClaim(new Claim(SystemAdminClaim, "true"));
    }

    private static async Task ValidateCookiePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var userIdClaim = ctx.Principal?.FindFirst(UserIdClaim)?.Value;
        if (!long.TryParse(userIdClaim, out var userId))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieScheme).ConfigureAwait(false);
            return;
        }

        var db = ctx.HttpContext.RequestServices.GetRequiredService<BeaconDbContext>();
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
        if (user is null || user.IsDisabled)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieScheme).ConfigureAwait(false);
            return;
        }

        // Refresh sysadmin claim — privilege changes mid-session shouldn't
        // require the user to sign out and back in.
        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        var hadClaim = identity.HasClaim(SystemAdminClaim, "true");
        if (user.IsSystemAdmin && !hadClaim)
        {
            identity.AddClaim(new Claim(SystemAdminClaim, "true"));
            ctx.ShouldRenew = true;
        }
        else if (!user.IsSystemAdmin && hadClaim)
        {
            var c = identity.FindFirst(SystemAdminClaim);
            if (c is not null) identity.RemoveClaim(c);
            ctx.ShouldRenew = true;
        }
    }
}
