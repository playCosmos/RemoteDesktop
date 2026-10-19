using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace RemoteDesktop;

internal static class DesktopThread
{
    private static readonly DesktopInputQueue queue = new();
    internal const int QueueCapacity = 64;
    public static void EnsureStarted() { _ = queue; }
    public static void AttachPump(Func<bool> callback) => queue.AttachPump(callback);
    internal static int PendingCount => queue.PendingCount;
    internal static int PendingPriorityCount => queue.PendingPriorityCount;
    public static T Invoke<T>(Func<T> callback) => queue.Invoke(callback, CancellationToken.None, priority: false);
    public static T Invoke<T>(Func<T> callback, CancellationToken cancellationToken) => queue.Invoke(callback, cancellationToken, priority: false);
    public static void Invoke(Action callback) => queue.Invoke(() => { callback(); return true; }, CancellationToken.None, priority: false);
    public static void Invoke(Action callback, CancellationToken cancellationToken) => queue.Invoke(() => { callback(); return true; }, cancellationToken, priority: false);
    public static T InvokePriority<T>(Func<T> callback, CancellationToken cancellationToken = default)
        => queue.Invoke(callback, cancellationToken, priority: true, critical: false);
    public static void InvokePriority(Action callback, CancellationToken cancellationToken = default)
        => queue.Invoke(() => { callback(); return true; }, cancellationToken, priority: true, critical: false);
    public static void InvokeCritical(Action callback)
        => queue.Invoke(() => { callback(); return true; }, CancellationToken.None, priority: true, critical: true);

    private sealed class DesktopInputQueue
    {
        private const int AdmissionTimeoutMilliseconds = 250;
        private readonly BlockingCollection<Action> work = new(new ConcurrentQueue<Action>(), QueueCapacity);
        private readonly BlockingCollection<Action> priorityWork = new(new ConcurrentQueue<Action>(), 8);
        private readonly AutoResetEvent workAvailable = new(false);
        private Func<bool>? smoothPump;
        private bool prioritySet;
        private int workerThreadId;

        public int PendingCount => work.Count;
        public int PendingPriorityCount => priorityWork.Count;

        public DesktopInputQueue()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Remote Desktop input desktop" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void AttachPump(Func<bool> callback) => smoothPump = callback;

        public T Invoke<T>(Func<T> callback, CancellationToken cancellationToken, bool priority, bool critical = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref workerThreadId)) return callback();

            var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = completed.Task.ContinueWith(
                task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            Action item = () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completed.TrySetCanceled(cancellationToken);
                    return;
                }
                try { completed.TrySetResult(callback()); }
                catch (Exception error) { completed.TrySetException(error); }
            };

            if (priority)
            {
                if (critical) priorityWork.Add(item);
                else if (!priorityWork.TryAdd(item, AdmissionTimeoutMilliseconds, cancellationToken))
                    throw new DesktopInputQueueFullException();
                workAvailable.Set();
            }
            else if (!work.TryAdd(item, AdmissionTimeoutMilliseconds, cancellationToken))
            {
                throw new DesktopInputQueueFullException();
            }
            else workAvailable.Set();

            return completed.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }

        private void Run()
        {
            Volatile.Write(ref workerThreadId, Environment.CurrentManagedThreadId);
            while (true)
            {
                ApplyPriority();
                if (!Desktop.SmoothCapture)
                {
                    if (TakeNext(out var idle, 250)) idle();
                    continue;
                }
                if (TakeNext(out var callback, 0))
                {
                    callback();
                    continue;
                }
                if (smoothPump?.Invoke() == true) continue;
                if (TakeNext(out callback, 15)) callback();
            }
        }

        private bool TakeNext(out Action callback, int timeoutMilliseconds)
        {
            var started = Environment.TickCount64;
            while (true)
            {
                if (priorityWork.TryTake(out callback!)) return true;
                if (work.TryTake(out callback!)) return true;
                var remaining = timeoutMilliseconds - (int)(Environment.TickCount64 - started);
                if (remaining <= 0 || !workAvailable.WaitOne(remaining)) break;
            }
            callback = null!;
            return false;
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

internal sealed class DesktopInputQueueFullException()
    : InvalidOperationException("The remote desktop input queue is full.");
