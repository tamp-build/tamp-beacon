using System;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Models;

namespace Tamp.Beacon.Api;

public static class ProjectTokensEndpoints
{
    public static IEndpointRouteBuilder MapProjectTokens(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{slug}/tokens").RequireAuthorization();

        group.MapGet("", ListAsync);
        group.MapPost("", MintAsync);
        group.MapDelete("/{tokenId:long}", RevokeAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        // Tokens are an admin-only surface — viewers can see member rosters
        // but not ingest credentials, even hashed.
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var rows = await db.ProjectTokens.AsNoTracking()
            .Where(t => t.ProjectId == gate.Project!.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TokenSummary
            {
                Id = t.Id,
                Label = t.Label,
                CreatedAt = t.CreatedAt,
                LastUsedAt = t.LastUsedAt,
                RevokedAt = t.RevokedAt,
                CreatedByUsername = t.CreatedBy.Username,
            })
            .ToListAsync(cancel).ConfigureAwait(false);
        return Results.Ok(new { tokens = rows });
    }

    private static async Task<IResult> MintAsync(
        string slug,
        MintTokenRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        ProjectTokenService tokens,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        if (string.IsNullOrWhiteSpace(body.Label))
            return Results.BadRequest(new { error = "label is required" });

        var userId = ProjectAuthorization.CurrentUserId(ctx)!.Value;
        var minted = await tokens.MintAsync(gate.Project!.Id, userId, body.Label, cancel).ConfigureAwait(false);

        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "project.token_minted",
            ActorUserId = userId,
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = DateTimeOffset.UtcNow,
            DetailJson = $"{{\"slug\":\"{slug}\",\"label\":{System.Text.Json.JsonSerializer.Serialize(minted.Row.Label)}}}",
        });
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);

        var creator = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancel).ConfigureAwait(false);
        return Results.Created($"/api/projects/{slug}/tokens/{minted.Row.Id}", new MintedTokenResponse
        {
            Id = minted.Row.Id,
            Label = minted.Row.Label,
            CreatedAt = minted.Row.CreatedAt,
            CreatedByUsername = creator.Username,
            Token = minted.Plaintext,
        });
    }

    private static async Task<IResult> RevokeAsync(
        string slug,
        long tokenId,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        ProjectTokenService tokens,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Admin, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var token = await db.ProjectTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.ProjectId == gate.Project!.Id, cancel)
            .ConfigureAwait(false);
        if (token is null) return Results.NotFound(new { error = "token not found" });

        var ok = await tokens.RevokeAsync(token.Id, cancel).ConfigureAwait(false);
        if (!ok) return Results.NotFound(new { error = "token not found" });

        db.AuthAuditLog.Add(new AuthAuditLogEntry
        {
            Event = "project.token_revoked",
            ActorUserId = ProjectAuthorization.CurrentUserId(ctx),
            RemoteIp = ctx.Connection.RemoteIpAddress?.ToString(),
            AtUtc = DateTimeOffset.UtcNow,
            DetailJson = $"{{\"slug\":\"{slug}\",\"label\":{System.Text.Json.JsonSerializer.Serialize(token.Label)}}}",
        });
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);
        return Results.NoContent();
    }

    public sealed record MintTokenRequest
    {
        [JsonPropertyName("label")] public string Label { get; init; } = "";
    }

    public sealed record TokenSummary
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("label")] public string Label { get; init; } = "";
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
        [JsonPropertyName("last_used_at")] public DateTimeOffset? LastUsedAt { get; init; }
        [JsonPropertyName("revoked_at")] public DateTimeOffset? RevokedAt { get; init; }
        [JsonPropertyName("created_by_username")] public string CreatedByUsername { get; init; } = "";
    }

    public sealed record MintedTokenResponse
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("label")] public string Label { get; init; } = "";
        [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; init; }
        [JsonPropertyName("created_by_username")] public string CreatedByUsername { get; init; } = "";
        /// <summary>Plaintext bearer token — shown exactly once at mint time.</summary>
        [JsonPropertyName("token")] public string Token { get; init; } = "";
    }
}
