using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Slice 1 surface: binds <see cref="AuthOptions"/>, registers the password
/// hasher, and wires the setup-token bootstrap printer as a hosted service.
/// Slice 2 extends this with the GitHub OIDC JwtBearer pipeline + cookie
/// session for the admin local login.
/// </summary>
public static class AuthExtensions
{
    public static IServiceCollection AddBeaconAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<AuthOptions>()
            .Bind(config.GetSection("Beacon:Auth"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<PasswordHasher>();
        services.AddHostedService<SetupTokenManager>();
        return services;
    }
}
