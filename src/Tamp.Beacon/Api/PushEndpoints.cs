using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Models;
using Tamp.Beacon.Push;

namespace Tamp.Beacon.Api;

/// <summary>
/// Web Push subscription surface. Subscriptions are scoped to (user,
/// project) — a viewer of a project can opt into failure alerts; a
/// non-member cannot subscribe and gets the standard 404. The VAPID
/// public key is exposed publicly for the SPA's
/// <c>pushManager.subscribe()</c> call (the public key isn't a secret).
/// </summary>
public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPush(this IEndpointRouteBuilder app)
    {
        // Public — the SPA needs this BEFORE the user signs in if you want
        // the "subscribe" UI to be reactive after login without a refetch.
        app.MapGet("/api/push/vapid-public-key", (VapidKeyStore keys) =>
            Results.Ok(new { public_key = keys.PublicKey }));

        var group = app.MapGroup("/api/projects/{slug}/push").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapPost("/subscribe", SubscribeAsync);
        group.MapPost("/unsubscribe", UnsubscribeAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        string slug,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        var userId = ProjectAuthorization.CurrentUserId(ctx)!.Value;
        var rows = await db.PushSubscriptions.AsNoTracking()
            .Where(s => s.ProjectId == gate.Project!.Id && s.UserId == userId)
            .Select(s => new SubscriptionSummary
            {
                Id = s.Id,
                Endpoint = s.Endpoint,
                CreatedUnixNs = s.CreatedUnixNs,
            })
            .ToListAsync(cancel).ConfigureAwait(false);
        return Results.Ok(new { subscriptions = rows });
    }

    private static async Task<IResult> SubscribeAsync(
        string slug,
        SubscribeRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        if (string.IsNullOrWhiteSpace(body.Endpoint))
            return Results.BadRequest(new { error = "endpoint is required" });
        if (body.Keys is null ||
            string.IsNullOrWhiteSpace(body.Keys.P256dh) ||
            string.IsNullOrWhiteSpace(body.Keys.Auth))
            return Results.BadRequest(new { error = "keys.p256dh and keys.auth are required" });

        var userId = ProjectAuthorization.CurrentUserId(ctx)!.Value;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

        // Endpoint is the natural key (one browser = one endpoint URL).
        // Upsert against it — re-subscribing on the same browser claims
        // ownership for the currently-signed-in user.
        var existing = await db.PushSubscriptions
            .FirstOrDefaultAsync(s => s.Endpoint == body.Endpoint, cancel).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.P256dh = body.Keys.P256dh;
            existing.Auth = body.Keys.Auth;
            existing.UserId = userId;
            existing.ProjectId = gate.Project!.Id;
            await db.SaveChangesAsync(cancel).ConfigureAwait(false);
            return Results.Ok(new SubscribeResponse { Id = existing.Id, Updated = true });
        }

        var entity = new PushSubscription
        {
            Endpoint = body.Endpoint,
            P256dh = body.Keys.P256dh,
            Auth = body.Keys.Auth,
            UserId = userId,
            ProjectId = gate.Project!.Id,
            CreatedUnixNs = now,
        };
        db.PushSubscriptions.Add(entity);
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);
        return Results.Created(
            $"/api/projects/{slug}/push/{entity.Id}",
            new SubscribeResponse { Id = entity.Id, Updated = false });
    }

    private static async Task<IResult> UnsubscribeAsync(
        string slug,
        UnsubscribeRequest body,
        HttpContext ctx,
        BeaconDbContext db,
        ProjectAuthorization auth,
        CancellationToken cancel)
    {
        var gate = await auth.RequireAsync(ctx, slug, ProjectRole.Viewer, cancel).ConfigureAwait(false);
        if (gate.Gate != ProjectGate.Ok) return ProjectsEndpoints.Map(gate);

        if (string.IsNullOrWhiteSpace(body.Endpoint))
            return Results.BadRequest(new { error = "endpoint is required" });

        var userId = ProjectAuthorization.CurrentUserId(ctx)!.Value;
        // Scope the delete to (current user, this project, this endpoint) —
        // a stale endpoint that's drifted to another project still belongs
        // to the same browser; the user signed-out it from this project.
        var row = await db.PushSubscriptions
            .FirstOrDefaultAsync(
                s => s.Endpoint == body.Endpoint && s.UserId == userId && s.ProjectId == gate.Project!.Id,
                cancel)
            .ConfigureAwait(false);
        if (row is null) return Results.NotFound(new { error = "subscription not found" });

        db.PushSubscriptions.Remove(row);
        await db.SaveChangesAsync(cancel).ConfigureAwait(false);
        return Results.NoContent();
    }

    public sealed record SubscribeRequest
    {
        [JsonPropertyName("endpoint")] public string Endpoint { get; init; } = "";
        [JsonPropertyName("keys")] public SubscribeKeys? Keys { get; init; }
    }

    public sealed record SubscribeKeys
    {
        [JsonPropertyName("p256dh")] public string P256dh { get; init; } = "";
        [JsonPropertyName("auth")] public string Auth { get; init; } = "";
    }

    public sealed record UnsubscribeRequest
    {
        [JsonPropertyName("endpoint")] public string Endpoint { get; init; } = "";
    }

    public sealed record SubscribeResponse
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("updated")] public bool Updated { get; init; }
    }

    public sealed record SubscriptionSummary
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("endpoint")] public string Endpoint { get; init; } = "";
        [JsonPropertyName("created_unix_ns")] public long CreatedUnixNs { get; init; }
    }
}
