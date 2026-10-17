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
        if (AppCommand.Has(args, AppCommand.InstallMachine)) return RunInstall(() => AppInstaller.InstallMachineHere());
        if (AppCommand.Has(args, AppCommand.UninstallMachine)) return RunInstall(() => AppInstaller.UninstallMachineHere());
        if (AppCommand.Has(args, AppCommand.Firewall)) return HostFirewall.ApplyCommand(args) ? 0 : 1;
        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }

    static int RunInstall(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(UiText.InstallFailed(ex), "Personal Remote Desktop", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }
}
