using System.Collections.Concurrent;

namespace RemoteDesktop;

public sealed class LoginThrottle
{
    private sealed record Failure(int Count, DateTime LastFailureUtc, DateTime RetryAfterUtc);
    private const int MaxSources = 1024;
    private static readonly TimeSpan RetainFor = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly ConcurrentDictionary<string, Failure> failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> inFlight = new(StringComparer.Ordinal);

    public int TrackedSources { get { lock (gate) return failures.Count; } }

    public bool TryAcquire(string source, DateTime nowUtc, out TimeSpan delay)
    {
        lock (gate)
        {
            Prune(nowUtc);
            if (failures.TryGetValue(source, out var failure) && failure.RetryAfterUtc > nowUtc)
            {
                delay = failure.RetryAfterUtc - nowUtc;
                return false;
            }
            if (!inFlight.TryAdd(source, 0))
            {
                delay = TimeSpan.FromSeconds(1);
                return false;
            }
            delay = TimeSpan.Zero;
            return true;
        }
    }

    public void Complete(string source)
    {
        lock (gate) inFlight.TryRemove(source, out _);
    }

    public bool TryGetDelay(string source, DateTime nowUtc, out TimeSpan delay)
    {
        lock (gate)
        {
            Prune(nowUtc);
            if (failures.TryGetValue(source, out var failure) && failure.RetryAfterUtc > nowUtc)
            {
                delay = failure.RetryAfterUtc - nowUtc;
                return true;
            }
            delay = TimeSpan.Zero;
            return false;
        }
    }

    public void Failed(string source, DateTime nowUtc)
    {
        lock (gate)
        {
            Prune(nowUtc);
            failures.TryGetValue(source, out var previous);
            if (previous is null && failures.Count >= MaxSources)
            {
                var oldest = failures.MinBy(pair => pair.Value.LastFailureUtc).Key;
                if (oldest is not null) failures.TryRemove(oldest, out _);
            }
            var count = Math.Min(32, (previous?.Count ?? 0) + 1);
            var exponent = Math.Min(count - 1, 8);
            var seconds = Math.Min(300, 2 << exponent);
            failures[source] = new(count, nowUtc, nowUtc.AddSeconds(seconds));
        }
    }

    public void Succeeded(string source)
    {
        lock (gate) failures.TryRemove(source, out _);
    }

    private void Prune(DateTime nowUtc)
    {
        var expiredBefore = nowUtc - RetainFor;
        foreach (var source in failures.Where(pair => pair.Value.LastFailureUtc < expiredBefore).Select(pair => pair.Key).ToArray())
            failures.TryRemove(source, out _);
    }
}
