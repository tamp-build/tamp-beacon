using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Tamp.Beacon.Otlp;

/// <summary>
/// OTLP/JSON request envelope. Mirrors the subset of
/// <c>opentelemetry.proto.collector.trace.v1.ExportTraceServiceRequest</c>
/// that we care about. We don't pull the protobuf-generated types because:
/// (a) HTTP-JSON is the wire we accept, (b) the proto-codegen pipeline is
/// the whole reason gRPC is out of v0.1.0 scope, and (c) the OTLP JSON
/// schema is stable enough to hand-model.
/// </summary>
public sealed class ExportTraceServiceRequest
{
    [JsonPropertyName("resourceSpans")] public List<ResourceSpans> ResourceSpans { get; set; } = new();
}

public sealed class ResourceSpans
{
    [JsonPropertyName("resource")] public Resource? Resource { get; set; }
    [JsonPropertyName("scopeSpans")] public List<ScopeSpans> ScopeSpans { get; set; } = new();
}

public sealed class Resource
{
    [JsonPropertyName("attributes")] public List<KeyValue> Attributes { get; set; } = new();
}

public sealed class ScopeSpans
{
    [JsonPropertyName("scope")] public InstrumentationScope? Scope { get; set; }
    [JsonPropertyName("spans")] public List<Span> Spans { get; set; } = new();
}

public sealed class InstrumentationScope
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class Span
{
    [JsonPropertyName("traceId")] public string TraceId { get; set; } = "";
    [JsonPropertyName("spanId")] public string SpanId { get; set; } = "";
    [JsonPropertyName("parentSpanId")] public string? ParentSpanId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public int Kind { get; set; }
    [JsonPropertyName("startTimeUnixNano")] public string StartTimeUnixNano { get; set; } = "0";
    [JsonPropertyName("endTimeUnixNano")] public string EndTimeUnixNano { get; set; } = "0";
    [JsonPropertyName("attributes")] public List<KeyValue> Attributes { get; set; } = new();
    [JsonPropertyName("events")] public List<SpanEvent> Events { get; set; } = new();
    [JsonPropertyName("status")] public SpanStatus? Status { get; set; }
}

public sealed class SpanEvent
{
    [JsonPropertyName("timeUnixNano")] public string TimeUnixNano { get; set; } = "0";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("attributes")] public List<KeyValue> Attributes { get; set; } = new();
}

public sealed class SpanStatus
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class KeyValue
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public AnyValue? Value { get; set; }
}

public sealed class AnyValue
{
    [JsonPropertyName("stringValue")] public string? StringValue { get; set; }
    [JsonPropertyName("boolValue")] public bool? BoolValue { get; set; }
    [JsonPropertyName("intValue")] public string? IntValue { get; set; }  // OTLP encodes int64 as JSON string
    [JsonPropertyName("doubleValue")] public double? DoubleValue { get; set; }

    public object? Unwrap()
    {
        if (StringValue is not null) return StringValue;
        if (BoolValue is { } b) return b;
        if (IntValue is not null) return long.TryParse(IntValue, out var l) ? l : IntValue;
        if (DoubleValue is { } d) return d;
        return null;
    }
}

/// <summary>OTLP/JSON metrics envelope. Accepted and acked in v0.1.0; persistence is best-effort.</summary>
public sealed class ExportMetricsServiceRequest
{
    [JsonPropertyName("resourceMetrics")] public List<ResourceMetrics> ResourceMetrics { get; set; } = new();
}

public sealed class ResourceMetrics
{
    [JsonPropertyName("resource")] public Resource? Resource { get; set; }
    [JsonPropertyName("scopeMetrics")] public List<ScopeMetrics> ScopeMetrics { get; set; } = new();
}

public sealed class ScopeMetrics
{
    [JsonPropertyName("scope")] public InstrumentationScope? Scope { get; set; }
}
