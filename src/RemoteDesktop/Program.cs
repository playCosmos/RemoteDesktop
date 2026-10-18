namespace RemoteDesktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (AppCommand.Has(args, AppCommand.Service))
        {
            ServiceHost.Run();
            return 0;
        }
        if (AppCommand.Has(args, AppCommand.RemoveFirewall)) return HostFirewall.RemoveForProgram(Environment.ProcessPath) ? 0 : 1;
        if (AppCommand.Has(args, AppCommand.InstallMachine)) return RunOperation(() => AppInstaller.InstallMachineHere(), UiText.InstallFailed);
        if (AppCommand.Has(args, AppCommand.UninstallMachine)) return RunOperation(() => AppInstaller.UninstallMachineHere(), UiText.RemoveFailed);
        if (AppCommand.Has(args, AppCommand.Firewall)) return HostFirewall.ApplyCommand(args) ? 0 : 1;
        var app = new App();
        app.InitializeComponent();
        app.Run();
        return Environment.ExitCode;
    }

    static int RunOperation(Action action, Func<Exception, string> errorText)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(errorText(ex), "Personal Remote Desktop", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }
}
