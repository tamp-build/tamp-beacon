using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamp.Beacon;
using Tamp.Beacon.Api;
using Tamp.Beacon.Auth;

// Healthcheck CLI mode — Dockerfile's HEALTHCHECK invokes the binary with
// `--healthcheck` so the chiseled base image doesn't need curl baked in.
if (args is ["--healthcheck"])
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var resp = await http.GetAsync("http://localhost:8080/healthz").ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

// `tamp-beacon admin recover --username NAME` — mint a one-shot password-
// reset token and print it to stdout. Invoked from inside the running pod
// via `kubectl exec`. Trust boundary = pod-log readership.
if (args.Length >= 2 && args[0] == "admin" && args[1] == "recover")
{
    return await AdminRecoverCli.RunAsync(args[2..]).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

var envOverride = Environment.GetEnvironmentVariable("BEACON_DB_CONNECTION_STRING");
if (!string.IsNullOrWhiteSpace(envOverride))
{
    builder.Configuration["Beacon:ConnectionString"] = envOverride;
}

builder.Services.AddOptions<BeaconOptions>()
    .Bind(builder.Configuration.GetSection("Beacon"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddDbContext<BeaconDbContext>((sp, opts) =>
{
    var beacon = sp.GetRequiredService<IOptions<BeaconOptions>>().Value;
    opts.UseNpgsql(beacon.ConnectionString, npg => npg.MigrationsHistoryTable("__ef_migrations_history"));
    // EF 10's PendingModelChangesWarning fires false-positives when the
    // model + last-migration snapshot agree but the design-time / runtime
    // hash differ (jsonb columns + dual-purpose nav properties seem to
    // trigger it). The CLI's `has-pending-model-changes` is authoritative
    // — suppress the runtime warning so Migrate() doesn't throw on boot.
    opts.ConfigureWarnings(w => w.Ignore(
        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

builder.Services.AddBeaconAuth(builder.Configuration);

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Tamp.Beacon.Startup");
    try
    {
        await db.Database.MigrateAsync().ConfigureAwait(false);
        log.LogInformation("postgres migrations applied");
    }
    catch (Exception ex)
    {
        log.LogCritical(ex, "could not apply postgres migrations — /readyz will report 503 until this is resolved");
    }
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealth();
app.MapSetup();
app.MapAuth();
app.MapProjects();
app.MapProjectMembers();
app.MapProjectTokens();
app.MapAdminUsers();

app.Run();
return 0;

// Expose the implicit Program entry point so WebApplicationFactory<Program> works in tests.
public partial class Program;
