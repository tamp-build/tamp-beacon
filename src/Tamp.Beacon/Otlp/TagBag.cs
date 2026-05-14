using System.Collections.Generic;
using System.Text.Json;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;

namespace Tamp.Beacon.Otlp;

/// <summary>
/// Flattens an OTLP <see cref="KeyValue"/> attribute list to a
/// <see cref="Dictionary{TKey,TValue}"/> keyed by attribute name. The
/// <c>AnyValue</c> oneof is unwrapped to its concrete CLR type (string,
/// bool, long, double) so downstream code stops worrying about wire shape.
/// </summary>
public static class TagBag
{
    public static Dictionary<string, object?> Flatten(RepeatedField<KeyValue> attributes)
    {
        var bag = new Dictionary<string, object?>(attributes.Count);
        foreach (var kv in attributes)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            bag[kv.Key] = Unwrap(kv.Value);
        }
        return bag;
    }

    public static string? GetString(Dictionary<string, object?> bag, string key) =>
        bag.TryGetValue(key, out var v) ? v?.ToString() : null;

    public static long GetLong(Dictionary<string, object?> bag, string key, long fallback = 0)
    {
        if (!bag.TryGetValue(key, out var v) || v is null) return fallback;
        return v switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }

    public static int GetInt(Dictionary<string, object?> bag, string key, int fallback = 0) =>
        (int)GetLong(bag, key, fallback);

    public static double GetDouble(Dictionary<string, object?> bag, string key, double fallback = 0)
    {
        if (!bag.TryGetValue(key, out var v) || v is null) return fallback;
        return v switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };
    }

    public static string ToJson(Dictionary<string, object?> bag)
    {
        // Stable shape — JSON object with string keys, primitive-typed values.
        return JsonSerializer.Serialize(bag);
    }

    private static object? Unwrap(AnyValue? value)
    {
        if (value is null) return null;
        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue,
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            _ => null,
        };
    }
}
