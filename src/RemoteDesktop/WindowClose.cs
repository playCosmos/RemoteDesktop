namespace RemoteDesktop;

public static class WindowClose
{
    public enum Result
    {
        Hide,
        Ask,
        Exit
    }

    public static Result Decide(bool exitRequested, bool force, bool sessionLive)
    {
        if (!exitRequested) return Result.Hide;
        if (force || !sessionLive) return Result.Exit;
        return Result.Ask;
    }
}

public static class AppLifetime
{
    public static bool ExitRequested { get; private set; }
    public static bool ForceExit { get; private set; }

    public static void RequestExit(bool force = false, int exitCode = 0)
    {
        ExitRequested = true;
        ForceExit = force;
        Environment.ExitCode = exitCode;
        System.Windows.Application.Current?.Shutdown(exitCode);
    }

    public static void NoteSystemExit()
    {
        ExitRequested = true;
        ForceExit = true;
    }

    public static void Stay()
    {
        ExitRequested = false;
        ForceExit = false;
    }
}
