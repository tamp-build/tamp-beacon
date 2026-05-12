using System.Collections.Generic;
using System.Text.Json;

namespace Tamp.Beacon.Otlp;

/// <summary>
/// Flattens a list of OTLP key-value pairs into a tag dictionary the
/// downstream column-mappers can index, plus a JSON blob suitable for the
/// <c>raw_tags</c> column.
/// </summary>
internal static class TagBag
{
    public static Dictionary<string, object?> Flatten(IEnumerable<KeyValue> kvs)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in kvs)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            dict[kv.Key] = kv.Value?.Unwrap();
        }
        return dict;
    }

    public static string ToJson(IDictionary<string, object?> bag)
    {
        return JsonSerializer.Serialize(bag);
    }

    public static string? GetString(IDictionary<string, object?> bag, string key)
    {
        if (!bag.TryGetValue(key, out var v) || v is null) return null;
        return v as string ?? v.ToString();
    }

    public static long GetLong(IDictionary<string, object?> bag, string key, long fallback = 0)
    {
        if (!bag.TryGetValue(key, out var v) || v is null) return fallback;
        return v switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, out var p) => p,
            _ => fallback,
        };
    }

    public static int GetInt(IDictionary<string, object?> bag, string key, int fallback = 0)
        => (int)GetLong(bag, key, fallback);

    public static double GetDouble(IDictionary<string, object?> bag, string key, double fallback = 0)
    {
        if (!bag.TryGetValue(key, out var v) || v is null) return fallback;
        return v switch
        {
            double d => d,
            long l => l,
            int i => i,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            _ => fallback,
        };
    }
}
