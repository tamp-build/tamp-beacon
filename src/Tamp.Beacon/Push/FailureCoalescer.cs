using System.Collections.Concurrent;

namespace Tamp.Beacon.Push;

/// <summary>
/// Suppresses repeat failure alerts within a sliding window. Keyed by
/// <c>project + target</c>: a flaky build that fails 50× in a row emits
/// one notification per window per (project, target) pair, not 50.
/// </summary>
/// <remarks>
/// The "open questions" section of the v0.1.0 sketch pinned this as the
/// failure-alert rate-limit strategy. Implementation is in-memory; the
/// state is rebuilt on container restart, which means a restart can emit
/// a duplicate notification for an in-window failure. Acceptable cost
/// for v0.1.0 — alternative would be persisting the last-emit timestamps,
/// which adds a table and a write-path on the hot send loop for marginal
/// gain. Revisit if operators complain.
/// </remarks>
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
    public bool ShouldEmit(string projectName, string? targetName)
    {
        var key = MakeKey(projectName, targetName);
        var now = _clock();

        while (true)
        {
            if (_lastEmit.TryGetValue(key, out var prev))
            {
                if (now - prev < _window) return false;
                if (_lastEmit.TryUpdate(key, now, prev)) return true;
                // race: another thread updated; retry.
                continue;
            }
            if (_lastEmit.TryAdd(key, now)) return true;
            // race: another thread added; retry.
        }
    }

    public int TrackedKeyCount => _lastEmit.Count;

    private static string MakeKey(string projectName, string? targetName)
        => $"{projectName}{targetName ?? ""}";
}
