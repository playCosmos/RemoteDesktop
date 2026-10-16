namespace RemoteDesktop;

public partial class App : System.Windows.Application
{
    private Mutex? uiMutex;
    private bool ownsUiMutex;
    private EventWaitHandle? activationSignal;
    private Thread? activationWatcher;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!AcquireUiInstance()) return;
        ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Personal Remote Desktop 실행 오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private bool AcquireUiInstance()
    {
        const string mutexName = @"Local\PersonalRemoteDesktop.UI";
        const string eventName = @"Local\PersonalRemoteDesktop.UI.Activate";
        uiMutex = new Mutex(initiallyOwned: true, mutexName, out ownsUiMutex);
        if (!ownsUiMutex)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(eventName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
            uiMutex.Dispose();
            uiMutex = null;
            Shutdown();
            return false;
        }

        activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        var signalHandle = activationSignal;
        activationWatcher = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    signalHandle.WaitOne();
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront());
                }
                catch (ObjectDisposedException) { return; }
            }
        }) { IsBackground = true, Name = "UIActivation" };
        activationWatcher.Start();
        return true;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        activationSignal?.Dispose();
        activationSignal = null;
        if (uiMutex != null)
        {
            if (ownsUiMutex)
            {
                try { uiMutex.ReleaseMutex(); } catch (ApplicationException) { }
            }
            uiMutex.Dispose();
            uiMutex = null;
        }
        base.OnExit(e);
    }

}
