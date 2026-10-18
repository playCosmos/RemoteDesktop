namespace RemoteDesktop;

public sealed class SourceIntervalThrottle
{
    private const int MaxSources = 1024;
    private static readonly TimeSpan RetainFor = TimeSpan.FromMinutes(30);
    private readonly object gate = new();
    private readonly Dictionary<string, (DateTime LastAttemptUtc, DateTime RetryAfterUtc)> attempts = new(StringComparer.Ordinal);

    public bool TryAcquire(string source, DateTime nowUtc, TimeSpan interval, out TimeSpan retry)
    {
        lock (gate)
        {
            foreach (var stale in attempts.Where(pair => pair.Value.LastAttemptUtc < nowUtc - RetainFor).Select(pair => pair.Key).ToArray())
                attempts.Remove(stale);
            if (attempts.TryGetValue(source, out var previous) && previous.RetryAfterUtc > nowUtc)
            {
                retry = previous.RetryAfterUtc - nowUtc;
                return false;
            }
            if (!attempts.ContainsKey(source) && attempts.Count >= MaxSources)
            {
                var oldest = attempts.MinBy(pair => pair.Value.LastAttemptUtc).Key;
                if (oldest is not null) attempts.Remove(oldest);
            }
            attempts[source] = (nowUtc, nowUtc + interval);
            retry = TimeSpan.Zero;
            return true;
        }
    }
}
