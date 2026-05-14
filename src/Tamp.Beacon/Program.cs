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
using Tamp.Beacon.Otlp;
using Tamp.Beacon.Push;

// --- healthcheck CLI mode: HEALTHCHECK in the Dockerfile invokes the binary with --healthcheck
// instead of curling, so the chiseled base image doesn't need curl baked in.
if (args is ["--healthcheck"])
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var resp = await http.GetAsync("http://localhost:4318/healthz").ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<BeaconOptions>()
    .Bind(builder.Configuration.GetSection("Beacon"))
    .ValidateOnStart();

builder.Services.AddBeaconAuth(builder.Configuration);

builder.Services.AddDbContext<BeaconDbContext>((sp, opts) =>
{
    var beacon = sp.GetRequiredService<IOptions<BeaconOptions>>().Value;
    var path = beacon.DbPath;
    var dir = Path.GetDirectoryName(path);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    // WAL mode + NORMAL sync per the sketch's "open questions" answer for concurrent writers.
    opts.UseSqlite($"Data Source={path};Cache=Shared");
});

builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<BeaconOptions>>().Value;
    return new VapidKeyStore(opts.VapidKeyPath, opts.VapidSubject);
});

builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<BeaconOptions>>().Value;
    return new FailureCoalescer(TimeSpan.FromSeconds(Math.Max(1, opts.FailureAlertWindowSeconds)));
});

builder.Services.AddScoped<OtlpTraceReceiver>();
builder.Services.AddSingleton<OtlpMetricReceiver>();
builder.Services.AddScoped<WebPushSender>();
builder.Services.AddHostedService<FailureAlertWorker>();

// Configure JSON serialization to handle the OTLP/JSON case-sensitivity nuances.
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Static file serving: wwwroot is populated by Build.cs after `yarn build`.
builder.Services.AddDirectoryBrowser();

var app = builder.Build();

// Apply WAL + schema on boot. EnsureCreated is the v0.1.0 simplification —
// we don't carry migration history because the schema is greenfield. When
// it's time for a schema change, add EF migrations and remove this call.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
    await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;").ConfigureAwait(false);
    await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;").ConfigureAwait(false);

    // Force VAPID generation on boot so /healthz is non-blocking and the
    // public key is observable from the first request.
    _ = scope.ServiceProvider.GetRequiredService<VapidKeyStore>();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseBeaconAuth();

app.MapHealth();
app.MapOtlp();
app.MapBuilds();
app.MapProjects();
app.MapTargets();
app.MapPush();

// SPA fallback: any non-API GET falls through to index.html so client-side routing works.
app.MapFallback(async ctx =>
{
    var indexPath = Path.Combine(app.Environment.WebRootPath, "index.html");
    if (File.Exists(indexPath))
    {
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(indexPath).ConfigureAwait(false);
    }
    else
    {
        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsync(
            "tamp-beacon: dashboard SPA is not bundled in this build. Run `tamp FrontendBuild` to populate wwwroot/.")
            .ConfigureAwait(false);
    }
});

app.Run();
return 0;

// Expose the implicit Program entry point so WebApplicationFactory<Program> works in tests.
public partial class Program;
