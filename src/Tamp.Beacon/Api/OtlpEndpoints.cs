using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tamp.Beacon.Auth;
using Tamp.Beacon.Otlp;

namespace Tamp.Beacon.Api;

public static class OtlpEndpoints
{
    public static IEndpointRouteBuilder MapOtlp(this IEndpointRouteBuilder app)
    {
        var traces = app.MapPost("/v1/traces", IngestTracesAsync);
        var metrics = app.MapPost("/v1/metrics", IngestMetricsAsync);

        // When auth is enabled (Beacon:Auth:Mode = OidcGitHub), every OTLP request must carry a
        // verified GitHub Actions OIDC bearer. When disabled, [Authorize] would still 401, so we
        // gate the attribute on the bound options.
        var services = app.ServiceProvider;
        var authOpts = services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (authOpts.Mode == AuthMode.OidcGitHub)
        {
            traces.RequireAuthorization();
            metrics.RequireAuthorization();
        }
        return app;
    }

    private static async Task<IResult> IngestTracesAsync(
        ExportTraceServiceRequest payload,
        HttpContext httpContext,
        OtlpTraceReceiver receiver,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamp.Beacon.Otlp.Traces");
        try
        {
            var verifiedOrg = httpContext.GetVerifiedOrganization();
            var result = await receiver.IngestAsync(payload, verifiedOrg, ct);
            logger.LogInformation(
                "ingested builds={Builds} targets={Targets} commands={Commands} org={Organization}",
                result.BuildsIngested, result.TargetsIngested, result.CommandsIngested, verifiedOrg ?? "(unverified)");
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
