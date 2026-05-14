using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Models;
using Tamp.Beacon.Otlp;
using ExportTraceRequest = OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest;
using ExportMetricsRequest = OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceRequest;

namespace Tamp.Beacon.Api;

/// <summary>
/// OTLP/HTTP ingest. Two content types accepted — protobuf (the Tamp.Telemetry
/// default) and JSON (tolerant fallback). Every request must carry an
/// <c>Authorization: Bearer tbk_...</c> header resolving to an unrevoked
/// <see cref="ProjectToken"/>; presented tokens advance the row's
/// <c>last_used_at</c> watermark on every successful ingest.
/// </summary>
public static class OtlpEndpoints
{
    public static IEndpointRouteBuilder MapOtlp(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/traces", IngestTracesAsync);
        app.MapPost("/v1/metrics", IngestMetricsAsync);
        return app;
    }

    private static async Task<IResult> IngestTracesAsync(
        HttpContext httpContext,
        OtlpTraceReceiver receiver,
        BeaconDbContext db,
        ProjectTokenService tokens,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamp.Beacon.Otlp.Traces");
        var (token, project, gate) = await AuthorizeAsync(httpContext, db, tokens, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        ExportTraceRequest request;
        try
        {
            request = await ReadAsync(httpContext, ExportTraceRequest.Parser, ct).ConfigureAwait(false);
        }
        catch (BodyParseException ex)
        {
            logger.LogWarning("rejected OTLP trace payload: {Reason}", ex.Message);
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await receiver.IngestAsync(request, project!, ct).ConfigureAwait(false);
            await TouchTokenAsync(db, token!, ct).ConfigureAwait(false);
            logger.LogInformation(
                "ingested builds={Builds} targets={Targets} commands={Commands} project={Project}",
                result.BuildsIngested, result.TargetsIngested, result.CommandsIngested, project!.Slug);
            return Results.Ok(new { partialSuccess = new { rejectedSpans = 0 } });
        }
        catch (OtlpRejectionException ex)
        {
            logger.LogWarning("rejected non-Tamp trace payload: {Reason}", ex.Reason);
            return Results.UnprocessableEntity(new { error = ex.Reason });
        }
    }

    private static async Task<IResult> IngestMetricsAsync(
        HttpContext httpContext,
        OtlpMetricReceiver receiver,
        BeaconDbContext db,
        ProjectTokenService tokens,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamp.Beacon.Otlp.Metrics");
        var (token, _, gate) = await AuthorizeAsync(httpContext, db, tokens, ct).ConfigureAwait(false);
        if (gate is not null) return gate;

        ExportMetricsRequest request;
        try
        {
            request = await ReadAsync(httpContext, ExportMetricsRequest.Parser, ct).ConfigureAwait(false);
        }
        catch (BodyParseException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await receiver.IngestAsync(request, ct).ConfigureAwait(false);
            await TouchTokenAsync(db, token!, ct).ConfigureAwait(false);
            logger.LogDebug("ack metrics scopes={Scopes}", result.ScopesAcknowledged);
            return Results.Ok(new { partialSuccess = new { rejectedDataPoints = 0 } });
        }
        catch (OtlpRejectionException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Reason });
        }
    }

    private static async Task<(ProjectToken? token, Project? project, IResult? gate)> AuthorizeAsync(
        HttpContext ctx, BeaconDbContext db, ProjectTokenService tokens, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, Results.Json(
                new { error = "Authorization: Bearer <token> required" },
                statusCode: StatusCodes.Status401Unauthorized));
        }

        var plaintext = header["Bearer ".Length..].Trim();
        var token = await tokens.ResolveAsync(plaintext, ct).ConfigureAwait(false);
        if (token is null)
        {
            return (null, null, Results.Json(
                new { error = "invalid or revoked ingest token" },
                statusCode: StatusCodes.Status401Unauthorized));
        }

        var project = await db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == token.ProjectId && p.ArchivedAt == null, ct)
            .ConfigureAwait(false);
        if (project is null)
        {
            return (null, null, Results.Json(
                new { error = "project archived or missing" },
                statusCode: StatusCodes.Status401Unauthorized));
        }

        return (token, project, null);
    }

    private static async Task TouchTokenAsync(BeaconDbContext db, ProjectToken token, CancellationToken ct)
    {
        // Single UPDATE — no need to round-trip through the change tracker
        // because tokens.ResolveAsync returned an AsNoTracking row.
        var now = DateTimeOffset.UtcNow;
        await db.ProjectTokens
            .Where(t => t.Id == token.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), ct)
            .ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpContext ctx, MessageParser<T> parser, CancellationToken ct)
        where T : IMessage<T>, new()
    {
        var contentType = ctx.Request.ContentType ?? "";

        // Buffer the body fully — async stream reads via Task.Run can race
        // protobuf's synchronous ParseFrom, and the JSON path needs the
        // whole string anyway.
        await using var buffer = new System.IO.MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        if (contentType.Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("application/protobuf", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return parser.ParseFrom(bytes);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new BodyParseException($"invalid protobuf body: {ex.Message}");
            }
        }
        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            var json = System.Text.Encoding.UTF8.GetString(bytes);
            try
            {
                return JsonParser.Default.Parse<T>(json);
            }
            catch (InvalidProtocolBufferException ex)
            {
                throw new BodyParseException($"invalid OTLP/JSON body: {ex.Message}");
            }
        }
        throw new BodyParseException(
            $"unsupported Content-Type '{contentType}'; expected application/x-protobuf or application/json");
    }

    private sealed class BodyParseException : Exception
    {
        public BodyParseException(string message) : base(message) { }
    }
}
