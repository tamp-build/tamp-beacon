using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Tamp.Beacon.Api;

/// <summary>
/// Kubernetes-style probes. <c>/healthz</c> is liveness — returns 200 as long
/// as the process is up. <c>/readyz</c> is readiness — returns 200 only when
/// the DB connection succeeds and the schema is migrated (the singleton
/// <see cref="Models.SetupState"/> row is queryable).
/// </summary>
public static class HealthEndpoint
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/readyz", async (BeaconDbContext db, CancellationToken ct) =>
        {
            try
            {
                var state = await db.SetupStateEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == 1, ct)
                    .ConfigureAwait(false);

                var complete = state?.IsComplete ?? false;
                return Results.Ok(new
                {
                    status = "ready",
                    setup_complete = complete,
                    awaiting_setup = !complete,
                });
            }
            catch
            {
                return Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }
}
