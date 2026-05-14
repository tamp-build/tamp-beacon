using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Implementation of <c>tamp-beacon admin recover --username NAME</c>. Runs
/// inside the pod via <c>kubectl exec</c>, mints a one-shot reset token via
/// <see cref="PasswordResetService"/>, and prints the plaintext to stdout
/// where the operator can recover it. Exit code 0 on success, non-zero on
/// any error (missing flag, unknown user, etc) so shell pipelines can
/// branch reliably.
/// </summary>
public static class AdminRecoverCli
{
    public static async Task<int> RunAsync(string[] subArgs)
    {
        var username = ExtractFlag(subArgs, "--username") ?? ExtractFlag(subArgs, "-u");
        if (string.IsNullOrWhiteSpace(username))
        {
            await Console.Error.WriteLineAsync(
                "usage: tamp-beacon admin recover --username NAME").ConfigureAwait(false);
            return 2;
        }

        using var host = BuildCliHost();
        {
            using var scope = host.Services.CreateScope();
            var reset = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            var token = await reset.MintAsync(username!).ConfigureAwait(false);
            if (token is null)
            {
                await Console.Error.WriteLineAsync(
                    $"admin recover: user '{username}' not found or disabled").ConfigureAwait(false);
                return 3;
            }

            var ttl = scope.ServiceProvider.GetRequiredService<IOptions<AuthOptions>>().Value
                .PasswordResetTokenTtlSeconds.ToString(CultureInfo.InvariantCulture);

            var lines = new[]
            {
                "================================================================",
                $" tamp-beacon — password reset token for '{username}'",
                "----------------------------------------------------------------",
                $" token:    {token}",
                $" ttl:      {ttl}s",
                " consume:  POST /admin/recover  body: { username, token, new_password }",
                "================================================================",
            };
            foreach (var line in lines) Console.WriteLine(line);
        }
        return 0;
    }

    private static IHost BuildCliHost()
    {
        // Reuse the same config + DI shape the web host uses, minus the
        // hosted services. Notably the SetupTokenManager hosted service is
        // NOT registered so running the CLI doesn't mint a setup token.
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();

        var envOverride = Environment.GetEnvironmentVariable("BEACON_DB_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            builder.Configuration["Beacon:ConnectionString"] = envOverride;
        }

        builder.Services.AddOptions<BeaconOptions>()
            .Bind(builder.Configuration.GetSection("Beacon"))
            .ValidateDataAnnotations();
        builder.Services.AddOptions<AuthOptions>()
            .Bind(builder.Configuration.GetSection("Beacon:Auth"))
            .ValidateDataAnnotations();

        builder.Services.AddDbContext<BeaconDbContext>((sp, opts) =>
        {
            var beacon = sp.GetRequiredService<IOptions<BeaconOptions>>().Value;
            opts.UseNpgsql(beacon.ConnectionString);
            opts.ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        });
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddScoped<PasswordResetService>();

        return builder.Build();
    }

    private static string? ExtractFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == flag && i + 1 < args.Length) return args[i + 1];
            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
                return args[i][(flag.Length + 1)..];
        }
        return null;
    }
}
