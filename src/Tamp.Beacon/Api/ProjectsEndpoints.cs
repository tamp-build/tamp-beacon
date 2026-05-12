using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Sdk;

namespace Tamp.Beacon.Api;

public static class ProjectsEndpoints
{
    public static IEndpointRouteBuilder MapProjects(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects", ListProjectsAsync);
        return app;
    }

    private static async Task<IResult> ListProjectsAsync(BeaconDbContext db, CancellationToken ct)
    {
        // Group by (name, area), aggregating count + last-seen. Translates to a single
        // SQL GROUP BY in SQLite; cheap at v0.1.0 row counts.
        var rows = await db.Builds.AsNoTracking()
            .GroupBy(b => new { b.ProjectName, b.ProjectArea })
            .Select(g => new ProjectFacet
            {
                Name = g.Key.ProjectName,
                Area = g.Key.ProjectArea,
                LastSeenUnixNs = g.Max(b => b.StartedUnixNs),
                BuildsCount = g.LongCount(),
            })
            .OrderByDescending(p => p.LastSeenUnixNs)
            .ToListAsync(ct);

        return Results.Ok(new ProjectList { Projects = rows });
    }
}
