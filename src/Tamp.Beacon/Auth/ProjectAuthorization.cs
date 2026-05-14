using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Auth;

/// <summary>
/// Centralizes the SonarQube-style two-tier RBAC check. Every project
/// endpoint funnels through one of these gates so the membership rule
/// can't drift between endpoints. Non-members get 404 (not 403) for
/// project routes — TAM-214 threat-model decision: existence of a
/// project name is itself sensitive.
/// </summary>
public sealed class ProjectAuthorization
{
    private readonly BeaconDbContext _db;

    public ProjectAuthorization(BeaconDbContext db) => _db = db;

    /// <summary>Internal user id of the calling principal, or null when anonymous.</summary>
    public static long? CurrentUserId(HttpContext ctx)
    {
        var claim = ctx.User?.FindFirstValue(AuthExtensions.UserIdClaim);
        return long.TryParse(claim, out var id) ? id : null;
    }

    public static bool IsSystemAdmin(HttpContext ctx) =>
        ctx.User?.HasClaim(AuthExtensions.SystemAdminClaim, "true") ?? false;

    /// <summary>
    /// Loads the project + the caller's membership in one round-trip. Returns
    /// the gate result the endpoint should propagate. Non-existent projects
    /// AND projects the caller has no access to both return
    /// <see cref="ProjectGate.NotFound"/> — same 404 either way.
    /// </summary>
    public async Task<GateResult> RequireAsync(
        HttpContext ctx,
        string slug,
        ProjectRole minRole,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId(ctx);
        if (userId is null) return GateResult.Unauthenticated();

        var project = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == slug && p.ArchivedAt == null, ct)
            .ConfigureAwait(false);
        if (project is null) return GateResult.NotFound();

        var sysadmin = IsSystemAdmin(ctx);
        ProjectMember? membership = null;
        if (!sysadmin)
        {
            membership = await _db.ProjectMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == userId.Value, ct)
                .ConfigureAwait(false);
            if (membership is null) return GateResult.NotFound();
            if (membership.Role < minRole) return GateResult.Forbidden();
        }

        return GateResult.Ok(project, membership?.Role ?? ProjectRole.Admin);
    }

    /// <summary>
    /// Returns the subset of project ids the caller can see — sysadmins see
    /// everything, everyone else sees only their memberships. Excludes
    /// archived projects.
    /// </summary>
    public async Task<long[]> VisibleProjectIdsAsync(HttpContext ctx, CancellationToken ct = default)
    {
        var userId = CurrentUserId(ctx);
        if (userId is null) return System.Array.Empty<long>();

        if (IsSystemAdmin(ctx))
        {
            return await _db.Projects.AsNoTracking()
                .Where(p => p.ArchivedAt == null)
                .Select(p => p.Id)
                .ToArrayAsync(ct).ConfigureAwait(false);
        }

        return await _db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == userId.Value && m.Project.ArchivedAt == null)
            .Select(m => m.ProjectId)
            .ToArrayAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Outcome of a <see cref="ProjectAuthorization.RequireAsync"/> call.</summary>
public sealed record GateResult
{
    public ProjectGate Gate { get; init; }
    public Project? Project { get; init; }
    public ProjectRole EffectiveRole { get; init; }

    public static GateResult Ok(Project p, ProjectRole role) =>
        new() { Gate = ProjectGate.Ok, Project = p, EffectiveRole = role };
    public static GateResult Unauthenticated() => new() { Gate = ProjectGate.Unauthenticated };
    public static GateResult NotFound() => new() { Gate = ProjectGate.NotFound };
    public static GateResult Forbidden() => new() { Gate = ProjectGate.Forbidden };
}

public enum ProjectGate
{
    Ok,
    Unauthenticated,
    NotFound,
    Forbidden,
}
