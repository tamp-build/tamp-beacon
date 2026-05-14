using System;
using System.Collections.Concurrent;

namespace Tamp.Beacon.Push;

/// <summary>
/// Suppresses repeat failure alerts within a sliding window. Keyed by
/// <c>project_id + target_name</c>: a flaky build that fails 50× in a row
/// emits one notification per window per (project, target) pair, not 50.
/// State is in-memory; container restart resets the window, which can
/// emit one duplicate notification on a hot failure — acceptable cost
/// vs. a write-path on the alert loop.
/// </summary>
public sealed class FailureCoalescer
{
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEmit = new(StringComparer.Ordinal);

    public FailureCoalescer(TimeSpan window) : this(window, () => DateTimeOffset.UtcNow) { }

    public FailureCoalescer(TimeSpan window, Func<DateTimeOffset> clock)
    {
        _window = window;
        _clock = clock;
    }

    /// <summary>
    /// Returns true if a notification should be emitted for the given key.
    /// Updates the last-emit timestamp atomically on positive return.
    /// </summary>
    public bool ShouldEmit(long projectId, string? targetName)
    {
        var key = $"{projectId}|{targetName ?? string.Empty}";
        var now = _clock();

        while (true)
        {
            if (_lastEmit.TryGetValue(key, out var prev))
            {
                if (now - prev < _window) return false;
                if (_lastEmit.TryUpdate(key, now, prev)) return true;
                continue;  // lost the race; retry
            }
            if (_lastEmit.TryAdd(key, now)) return true;
        }
    }

    public int TrackedKeyCount => _lastEmit.Count;
}
