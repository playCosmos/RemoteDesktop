namespace RemoteDesktop;

public sealed class LoginThrottle
{
    private sealed record Failure(int Count, DateTime LastFailureUtc, DateTime RetryAfterUtc);
    private const int MaxSources = 1024;
    private readonly object gate = new();
    private readonly Dictionary<string, Failure> failures = new(StringComparer.Ordinal);

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
                if (oldest is not null) failures.Remove(oldest);
            }
            var count = Math.Min(32, (previous?.Count ?? 0) + 1);
            var exponent = Math.Min(count - 1, 9);
            var seconds = Math.Min(300, 1 << exponent);
            failures[source] = new(count, nowUtc, nowUtc.AddSeconds(seconds));
        }
    }

    public void Succeeded(string source)
    {
        lock (gate) failures.Remove(source);
    }

    private void Prune(DateTime nowUtc)
    {
        var expiredBefore = nowUtc - TimeSpan.FromMinutes(30);
        foreach (var source in failures.Where(pair => pair.Value.LastFailureUtc < expiredBefore).Select(pair => pair.Key).ToArray())
            failures.Remove(source);
    }
}
