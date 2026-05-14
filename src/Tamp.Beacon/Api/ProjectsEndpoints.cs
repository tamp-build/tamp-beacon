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
        app.MapGet("/api/organizations", ListOrganizationsAsync);
        return app;
    }

    private static async Task<IResult> ListProjectsAsync(BeaconDbContext db, CancellationToken ct)
    {
        // Group by (organization, name, area), aggregating count + last-seen.
        var rows = await db.Builds.AsNoTracking()
            .GroupBy(b => new { b.Organization, b.ProjectName, b.ProjectArea })
            .Select(g => new ProjectFacet
            {
                Organization = g.Key.Organization,
                Name = g.Key.ProjectName,
                Area = g.Key.ProjectArea,
                LastSeenUnixNs = g.Max(b => b.StartedUnixNs),
                BuildsCount = g.LongCount(),
                FailedCount = g.LongCount(b => b.Outcome == "failure"),
            })
            .OrderByDescending(p => p.LastSeenUnixNs)
            .ToListAsync(ct);

        return Results.Ok(new ProjectList { Projects = rows });
    }

    private static async Task<IResult> ListOrganizationsAsync(BeaconDbContext db, CancellationToken ct)
    {
        // Top-level rollup: one row per Organization with totals across all its projects.
        // Powers the tree-view landing — Org → Project → Area drilldown.
        var rows = await db.Builds.AsNoTracking()
            .GroupBy(b => b.Organization)
            .Select(g => new OrganizationFacet
            {
                Name = g.Key,
                ProjectsCount = g.Select(b => b.ProjectName).Distinct().Count(),
                BuildsCount = g.LongCount(),
                FailedCount = g.LongCount(b => b.Outcome == "failure"),
                LastSeenUnixNs = g.Max(b => b.StartedUnixNs),
            })
            .OrderByDescending(o => o.LastSeenUnixNs)
            .ToListAsync(ct);

        return Results.Ok(new OrganizationList { Organizations = rows });
    }
}
