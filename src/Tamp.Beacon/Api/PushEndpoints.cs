using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Tamp.Beacon.Models;
using Tamp.Beacon.Sdk;

namespace Tamp.Beacon.Api;

public static class PushEndpoints
{
    public static IEndpointRouteBuilder MapPush(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/push/subscribe", SubscribeAsync);
        app.MapDelete("/api/push/subscribe", UnsubscribeAsync);
        return app;
    }

    private static async Task<IResult> SubscribeAsync(
        BeaconDbContext db,
        PushSubscriptionRequest body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Endpoint))
            return Results.BadRequest(new { error = "endpoint is required" });
        if (body.Keys is null || string.IsNullOrWhiteSpace(body.Keys.P256dh) || string.IsNullOrWhiteSpace(body.Keys.Auth))
            return Results.BadRequest(new { error = "keys.p256dh and keys.auth are required" });

        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == body.Endpoint, ct);
        if (existing is not null)
        {
            existing.P256dh = body.Keys.P256dh;
            existing.Auth = body.Keys.Auth;
            existing.ProjectFilter = body.ProjectFilter;
            existing.AreaFilter = body.AreaFilter;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = existing.Id, updated = true });
        }

        var entity = new PushSubscription
        {
            Endpoint = body.Endpoint,
            P256dh = body.Keys.P256dh,
            Auth = body.Keys.Auth,
            ProjectFilter = body.ProjectFilter,
            AreaFilter = body.AreaFilter,
            CreatedUnixNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
        };
        db.PushSubscriptions.Add(entity);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/push/subscribe/{entity.Id}", new { id = entity.Id, updated = false });
    }

    private static async Task<IResult> UnsubscribeAsync(
        BeaconDbContext db,
        string endpoint,
        CancellationToken ct)
    {
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is null) return Results.NotFound();
        db.PushSubscriptions.Remove(existing);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
