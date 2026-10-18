namespace RemoteDesktop;

internal static class ShutdownCleanup
{
    internal static async Task RunAsync(IEnumerable<(string Name, Func<Task> Cleanup)> steps, Action<string, Exception> report)
    {
        foreach (var (name, cleanup) in steps)
        {
            try { await cleanup(); }
            catch (Exception error)
            {
                try { report(name, error); }
                catch (Exception) { }
            }
        }
    }
}
