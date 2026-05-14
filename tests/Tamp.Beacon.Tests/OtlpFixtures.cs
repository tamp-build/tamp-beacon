using System;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;
using ExportTraceRequest = OpenTelemetry.Proto.Collector.Trace.V1.ExportTraceServiceRequest;

namespace Tamp.Beacon.Tests;

/// <summary>
/// Hand-built OTLP fixtures that match the ADR-0018 tag contract. Helpers
/// produce both protobuf bytes and OTLP/JSON strings from the same logical
/// payload so wire-format coverage is symmetric.
/// </summary>
internal static class OtlpFixtures
{
    public static ExportTraceRequest TrivialBuildSuccess(
        string project = "HoldFast",
        string? area = "frontend",
        string traceIdHex = "0011223344556677889900aabbccddee",
        string spanIdHex = "1122334455667788",
        string outcome = "success",
        int targetsTotal = 5,
        int targetsFailed = 0)
    {
        var span = new OtlpSpan
        {
            TraceId = ByteString.CopyFrom(HexToBytes(traceIdHex)),
            SpanId = ByteString.CopyFrom(HexToBytes(spanIdHex)),
            Name = "Ci",
            Kind = OtlpSpan.Types.SpanKind.Internal,
            StartTimeUnixNano = 1_700_000_000_000_000_000UL,
            EndTimeUnixNano = 1_700_000_123_000_000_000UL,
        };

        AddString(span, "tamp.build.project.name", project);
        if (area is not null) AddString(span, "tamp.build.project.area", area);
        AddString(span, "tamp.build.organization", "BrewingCoder");
        AddString(span, "tamp.build.cli_version", "1.9.0");
        AddLong(span, "tamp.build.duration_ns", 123_000_000_000);
        AddLong(span, "tamp.build.exit_code", outcome == "success" ? 0 : 1);
        AddLong(span, "tamp.build.targets.total", targetsTotal);
        AddLong(span, "tamp.build.targets.failed", targetsFailed);
        AddString(span, "tamp.host.os", "linux");
        AddString(span, "tamp.host.arch", "x64");
        AddString(span, "tamp.ci.vendor", "github");
        AddString(span, "outcome", outcome);

        span.Events.Add(new OtlpSpan.Types.Event
        {
            TimeUnixNano = 1_700_000_122_000_000_000UL,
            Name = "tamp.build.summary",
        });

        return new ExportTraceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = "Tamp.Build", Version = "1.9.0" },
                            Spans = { span },
                        },
                    },
                },
            },
        };
    }

    public static ExportTraceRequest NonTampScope()
    {
        return new ExportTraceRequest
        {
            ResourceSpans =
            {
                new ResourceSpans
                {
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = new InstrumentationScope { Name = "SomeOther.Source" },
                        },
                    },
                },
            },
        };
    }

    public static byte[] ToProto(IMessage request) => request.ToByteArray();
    public static string ToJson(IMessage request) =>
        Google.Protobuf.JsonFormatter.Default.Format(request);

    private static void AddString(OtlpSpan span, string key, string value) =>
        span.Attributes.Add(new KeyValue { Key = key, Value = new AnyValue { StringValue = value } });

    private static void AddLong(OtlpSpan span, string key, long value) =>
        span.Attributes.Add(new KeyValue { Key = key, Value = new AnyValue { IntValue = value } });

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
