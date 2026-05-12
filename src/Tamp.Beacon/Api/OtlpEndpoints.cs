using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Tamp.Beacon.Otlp;

namespace Tamp.Beacon.Api;

public static class OtlpEndpoints
{
    public static IEndpointRouteBuilder MapOtlp(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/traces", IngestTracesAsync);
        app.MapPost("/v1/metrics", IngestMetricsAsync);
        return app;
    }

    private static async Task<IResult> IngestTracesAsync(
        ExportTraceServiceRequest payload,
        OtlpTraceReceiver receiver,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamp.Beacon.Otlp.Traces");
        try
        {
            var result = await receiver.IngestAsync(payload, ct);
            logger.LogInformation(
                "ingested builds={Builds} targets={Targets} commands={Commands}",
                result.BuildsIngested, result.TargetsIngested, result.CommandsIngested);
            return Results.Ok(new { partialSuccess = new { rejectedSpans = 0 } });
        }
        catch (OtlpRejectionException ex)
        {
            logger.LogWarning("rejected non-Tamp trace payload: {Reason}", ex.Reason);
            return Results.UnprocessableEntity(new { error = ex.Reason });
        }
    }

    private static async Task<IResult> IngestMetricsAsync(
        ExportMetricsServiceRequest payload,
        OtlpMetricReceiver receiver,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamp.Beacon.Otlp.Metrics");
        try
        {
            var result = await receiver.IngestAsync(payload, ct);
            logger.LogDebug("ack metrics scopes={Scopes}", result.ScopesAcknowledged);
            return Results.Ok(new { partialSuccess = new { rejectedDataPoints = 0 } });
        }
        catch (OtlpRejectionException ex)
        {
            logger.LogWarning("rejected non-Tamp metric payload: {Reason}", ex.Reason);
            return Results.UnprocessableEntity(new { error = ex.Reason });
        }
    }
}
