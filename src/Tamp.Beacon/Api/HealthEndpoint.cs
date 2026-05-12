using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Push;
using Tamp.Beacon.Sdk;

namespace Tamp.Beacon.Api;

public static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", async (
            BeaconDbContext db,
            VapidKeyStore vapid,
            IOptions<BeaconOptions> options,
            CancellationToken ct) =>
        {
            var rows = await CountAllAsync(db, ct);
            return Results.Ok(new HealthStatus
            {
                Status = "ok",
                DbPath = options.Value.DbPath,
                RowsTotal = rows,
                VapidPublicKey = vapid.PublicKey,
            });
        });
        return app;
    }

    private static async Task<long> CountAllAsync(BeaconDbContext db, CancellationToken ct)
    {
        var b = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.LongCountAsync(db.Builds, ct);
        var t = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.LongCountAsync(db.Targets, ct);
        var c = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.LongCountAsync(db.Commands, ct);
        var e = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.LongCountAsync(db.Events, ct);
        return b + t + c + e;
    }
}
