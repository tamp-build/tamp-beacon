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

var builder = WebApplication.CreateBuilder(args);

// BEACON_DB_CONNECTION_STRING is the explicit override knob for adopters who
// run against an external Postgres. Falls back to the bundled-Postgres Unix
// socket configured in BeaconOptions / appsettings.
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
});

builder.Services.AddBeaconAuth(builder.Configuration);

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// Apply pending migrations on boot. EnsureCreated is deliberately avoided —
// the v0.1.0 schema is large enough that we want migration history from day
// one so future ALTERs are tractable.
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
        // Do not throw: the process must stay up so /healthz answers and
        // the operator can inspect logs. /readyz already trips to 503 on
        // failed DB access.
    }
}

app.MapHealth();
app.MapSetup();

app.Run();
return 0;

// Expose the implicit Program entry point so WebApplicationFactory<Program> works in tests.
public partial class Program;
