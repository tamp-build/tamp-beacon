using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExportMetricsRequest = OpenTelemetry.Proto.Collector.Metrics.V1.ExportMetricsServiceRequest;

namespace Tamp.Beacon.Otlp;

/// <summary>
/// Receives OTLP metrics envelopes. v0.1.0 acks every Tamp metric payload
/// but does not persist counters / histograms — the trace stream is the
/// load-bearing signal for the dashboard, and metric storage is in the
/// 0.2.0 dashboard chart epic. Non-Tamp metrics still get rejected at the
/// ingress contract so misrouted senders see HTTP 422 immediately.
/// </summary>
public sealed class OtlpMetricReceiver
{
    public Task<MetricIngestResult> IngestAsync(
        ExportMetricsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scopes = request.ResourceMetrics
            .SelectMany(r => r.ScopeMetrics)
            .ToList();
        if (scopes.Count == 0)
            return Task.FromResult(new MetricIngestResult(0));

        foreach (var scope in scopes)
        {
            var name = scope.Scope?.Name ?? "";
            if (!name.StartsWith(OtlpTraceReceiver.TampSourcePrefix, StringComparison.Ordinal))
                throw new OtlpRejectionException(
                    $"this is tamp-beacon; route non-Tamp telemetry to your collector of choice (saw meter '{name}')");
        }

        // ack & drop: persistence reserved for 0.2.0.
        return Task.FromResult(new MetricIngestResult(scopes.Count));
    }
}

public sealed record MetricIngestResult(int ScopesAcknowledged);
