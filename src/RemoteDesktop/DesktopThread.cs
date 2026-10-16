using System.Collections.Concurrent;

namespace RemoteDesktop;

internal static class DesktopThread
{
    private static readonly DesktopInputQueue queue = new();
    public static void EnsureStarted() { _ = queue; }
    public static void AttachPump(Func<bool> callback) => queue.AttachPump(callback);
    public static T Invoke<T>(Func<T> callback) => queue.Invoke(callback);
    public static void Invoke(Action callback) => queue.Invoke(callback);

    private sealed class DesktopInputQueue
    {
        private readonly BlockingCollection<Action> work = new();
        private Func<bool>? smoothPump;
        private bool prioritySet;

        public DesktopInputQueue()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Remote Desktop input desktop" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void AttachPump(Func<bool> callback) => smoothPump = callback;

        public T Invoke<T>(Func<T> callback)
        {
            T? result = default;
            Exception? failure = null;
            using var completed = new ManualResetEventSlim();
            work.Add(() =>
            {
                try { result = callback(); }
                catch (Exception e) { failure = e; }
                finally { completed.Set(); }
            });
            completed.Wait();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return result!;
        }

        public void Invoke(Action callback) => Invoke(() => { callback(); return true; });

        private void Run()
        {
            while (!work.IsCompleted)
            {
                ApplyPriority();
                if (!Desktop.SmoothCapture)
                {
                    if (work.TryTake(out var idle, 250)) idle();
                    continue;
                }
                if (work.TryTake(out var callback))
                {
                    callback();
                    continue;
                }
                if (smoothPump?.Invoke() == true) continue;
                if (work.TryTake(out callback, 15)) callback();
            }
        }

        private void ApplyPriority()
        {
            if (prioritySet) return;
            if (Desktop.SmoothCapture) HostPerformance.ApplyCaptureThreadPriority();
            else return;
            prioritySet = true;
        }
    }
}
