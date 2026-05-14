using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Tamp.Beacon.Auth;

public static class AuthExtensions
{
    /// <summary>
    /// Custom claim name we stamp onto the verified principal so downstream code (the OTLP
    /// receiver) doesn't have to re-do the repo→org mapping on every span.
    /// </summary>
    public const string VerifiedOrganizationClaim = "tamp_beacon.organization";

    public static IServiceCollection AddBeaconAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AuthOptions>()
            .Bind(config.GetSection("Beacon:Auth"))
            .ValidateOnStart();

        // Resolve the bound options once so we know whether to wire JwtBearer at all.
        // Re-bound in the builder for the actual middleware below.
        var opts = new AuthOptions();
        config.GetSection("Beacon:Auth").Bind(opts);

        if (opts.Mode == AuthMode.Disabled)
        {
            // No authentication middleware. The OTLP endpoints still call AllowAnonymous-equivalent
            // since they're not [Authorize]'d; everything just passes through.
            return services;
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                jwt.Authority = opts.Issuer;
                jwt.RequireHttpsMetadata = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = opts.Issuer,
                    ValidateAudience = true,
                    ValidAudience = opts.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = OnTokenValidatedAsync,
                };
            });

        services.AddAuthorization();
        return services;
    }

    private static Task OnTokenValidatedAsync(TokenValidatedContext ctx)
    {
        var logger = ctx.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Tamp.Beacon.Auth");

        var opts = ctx.HttpContext.RequestServices
            .GetRequiredService<IOptions<AuthOptions>>().Value;

        var principal = ctx.Principal!;
        var repoOwner = principal.FindFirstValue("repository_owner");
        var repository = principal.FindFirstValue("repository");

        if (string.IsNullOrEmpty(repoOwner) || string.IsNullOrEmpty(repository))
        {
            logger.LogWarning("JWT missing repository_owner / repository claims; rejecting");
            ctx.Fail("required GitHub OIDC claims absent");
            return Task.CompletedTask;
        }

        var match = opts.Organizations.FirstOrDefault(o =>
            string.Equals(o.RepoOwner, repoOwner, StringComparison.OrdinalIgnoreCase) &&
            (o.Repo is null ||
             string.Equals(repository, $"{o.RepoOwner}/{o.Repo}", StringComparison.OrdinalIgnoreCase)));

        if (match is null)
        {
            logger.LogWarning(
                "no organization mapping for repository_owner={Owner} repository={Repo}; rejecting",
                repoOwner, repository);
            ctx.Fail("repository not in tamp-beacon allowlist");
            return Task.CompletedTask;
        }

        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(VerifiedOrganizationClaim, match.Organization));
        return Task.CompletedTask;
    }

    public static IApplicationBuilder UseBeaconAuth(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (opts.Mode == AuthMode.OidcGitHub)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }
        return app;
    }

    /// <summary>
    /// Returns the verified organization stamped onto the request principal, or null when auth
    /// is disabled / no token was supplied. The OTLP receiver uses this to override the
    /// tamp.build.organization tag on incoming spans so a leaked token can't submit telemetry
    /// for an organization the token isn't scoped to.
    /// </summary>
    public static string? GetVerifiedOrganization(this HttpContext ctx)
    {
        return ctx.User?.FindFirstValue(VerifiedOrganizationClaim);
    }
}
