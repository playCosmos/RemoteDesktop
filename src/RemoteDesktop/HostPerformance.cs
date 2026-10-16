using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace RemoteDesktop;

internal static class HostPerformance
{
    private static ProcessPriorityClass? previousPriority;

    public static void EnableHostPriority()
    {
        if (previousPriority.HasValue) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            var currentPriority = process.PriorityClass;
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
            previousPriority = currentPriority;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException) { }
    }

    public static void RestoreProcessPriority()
    {
        if (previousPriority is not { } priority) return;
        previousPriority = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = priority;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException or System.Security.SecurityException) { }
    }

    public static void ApplyCaptureThreadPriority() => SetCurrentThreadPriority(ThreadPriorityHighest);

    private static void SetCurrentThreadPriority(int priority)
    {
        var thread = GetCurrentThread();
        if (thread != IntPtr.Zero) SetThreadPriority(thread, priority);
    }

    private const int ThreadPriorityHighest = 2;

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadPriority(IntPtr thread, int priority);
}
