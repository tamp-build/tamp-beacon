using System;

namespace Tamp.Beacon.Otlp;

/// <summary>
/// Thrown by the OTLP receivers when an incoming payload is well-formed
/// but not Tamp-shaped (e.g. instrumentation-scope name doesn't start with
/// <c>Tamp.Build</c>). The endpoint converts this to HTTP 422.
/// </summary>
public sealed class OtlpRejectionException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
