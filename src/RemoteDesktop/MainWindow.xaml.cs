using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.ComponentModel;
using System.Diagnostics;

namespace RemoteDesktop;

public partial class MainWindow : Window
{
    private readonly HostSession hostSession;
    private readonly ClientSession clientSession;
    private bool initializing = true;
    private readonly string productTitle;
    private bool? stackedHostCards;
    private string? attentionPeer;
    private System.Windows.Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private bool trayExplained;
    private bool closeCleanupStarted;
    private bool closeCleanupComplete;
    internal long InputQueueWriteFailures => clientSession.InputQueueWriteFailures;

    public MainWindow()
    {
        hostSession = new HostSession(this);
        clientSession = new ClientSession(this);
        InitializeComponent();
        Activated += (_, _) => RefreshCapsLock();
        HostPort.TextChanged += (_, _) => hostSession.RefreshAddresses();
        Address.TextChanged += (_, _) => clientSession.NormalizePastedAddress();
        ServerPin.TextChanged += (_, _) => clientSession.UpdatePinSummary();
        HostPort.TextChanged += (_, _) => hostSession.Edited(HostPort);
        Address.TextChanged += (_, _) => clientSession.Edited(Address);
        ClientPort.TextChanged += (_, _) => clientSession.Edited(ClientPort);
        ServerPin.TextChanged += (_, _) => clientSession.Edited(ServerPin);
        ClientPassword.PasswordChanged += (_, _) => clientSession.PasswordEdited();
        HostPassword.PasswordChanged += (_, _) => hostSession.PasswordEdited();
        HostPasswordConfirm.PasswordChanged += (_, _) => hostSession.ConfirmEdited();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        productTitle = version is null ? "Personal Remote Desktop" : "Personal Remote Desktop " + version.ToString(3);
        Title = productTitle;
        ServerTab.Visibility = Visibility.Visible;
        ClientCapsLock.Text = HostCapsLock.Text = UiText.CapsLockWarning();
        hostSession.Bind();
        clientSession.Bind();
        initializing = false;
        SizeChanged += (_, _) => UpdateHostLayout();
        PreviewKeyDown += clientSession.FullscreenKey;
        SourceInitialized += (_, _) =>
        {
            clientSession.ApplyWindowPlacement();
            clientSession.InstallInputHooks();
        };
        Loaded += async (_, _) =>
        {
            UpdateHostLayout();
            if (AppCommand.Has(Environment.GetCommandLineArgs(), AppCommand.UiSmoke)) return;
            EnsureTray();
            await hostSession.StartIfConfigured();
            clientSession.FocusConnectField();
        };
        System.Windows.Application.Current.SessionEnding += (_, _) => AppLifetime.NoteSystemExit();
        Closing += OnWindowClosing;
        Activated += (_, _) => StopAttention();
        Deactivated += (_, _) => clientSession.QueueRelease();
        Activated += (_, _) => clientSession.WindowActivationChanged();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (closeCleanupComplete) return;
        e.Cancel = true;
        if (closeCleanupStarted) return;

        var decision = WindowClose.Decide(AppLifetime.ExitRequested, AppLifetime.ForceExit, UiText.ClosePrompt(clientSession.RemoteEndpoint, hostSession.ConnectedPeer) != null);
        if (decision == WindowClose.Result.Hide)
        {
            Hide();
            EnsureTray();
            NoteTray();
            return;
        }
        if (decision == WindowClose.Result.Ask && !ConfirmClose())
        {
            AppLifetime.Stay();
            return;
        }

        closeCleanupStarted = true;
        hostSession.Closing = true;
        Dispatcher.BeginInvoke(new Action(() => _ = FinishCloseAsync()));
    }

    private async Task FinishCloseAsync()
    {
        var steps = new (string Name, Func<Task> Cleanup)[]
        {
            ("save window placement", () => RunCloseAction(clientSession.SaveWindowPlacement)),
            ("release tray icon", () => RunCloseAction(ReleaseTray)),
            ("remove system shortcut hook", () => RunCloseAction(clientSession.UninstallSystemShortcutHook)),
            ("close fullscreen window", () => RunCloseAction(clientSession.CloseFullscreen)),
            ("disconnect client and clipboard", clientSession.DisconnectClient),
            ("dispose host session", hostSession.DisposeAsync),
            ("release tray icon", () => RunCloseAction(ReleaseTray))
        };
        try
        {
            await ShutdownCleanup.RunAsync(steps, (name, error) => Trace.TraceError("Shutdown stage {0} failed: {1}", name, error));
        }
        catch (Exception error) { Trace.TraceError("Shutdown sequence failed: {0}", error); }
        closeCleanupComplete = true;
        try { Close(); }
        catch (Exception error) { Trace.TraceError("Closing the window after cleanup failed: {0}", error); }
    }

    private static Task RunCloseAction(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    internal void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        NoteTray();
    }

    private void UpdateHostLayout(double? viewportWidth = null)
    {
        if (!IsInitialized || ActualWidth <= 0) return;
        var stacked = WindowLayout.StackHostPanels(viewportWidth ?? ActualWidth);
        if (stackedHostCards == stacked) return;
        stackedHostCards = stacked;
        var columns = HostContentGrid.ColumnDefinitions;
        if (stacked)
        {
            HostContentGrid.MaxWidth = 760;
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[0].MaxWidth = double.PositiveInfinity;
            columns[1].Width = new GridLength(0);
            columns[1].MaxWidth = 0;
            columns[2].Width = new GridLength(0);
            columns[2].MaxWidth = 0;
            Grid.SetRow(HostSetupColumn, 0);
            Grid.SetColumn(HostSetupColumn, 0);
            Grid.SetColumnSpan(HostSetupColumn, 3);
            Grid.SetRow(HostShareCard, 1);
            Grid.SetColumn(HostShareCard, 0);
            Grid.SetColumnSpan(HostShareCard, 3);
            HostShareCard.Margin = new Thickness(0, 16, 0, 0);
        }
        else
        {
            HostContentGrid.MaxWidth = 980;
            columns[0].Width = new GridLength(1, GridUnitType.Star);
            columns[0].MaxWidth = 520;
            columns[1].Width = new GridLength(20);
            columns[1].MaxWidth = 20;
            columns[2].Width = new GridLength(1, GridUnitType.Star);
            columns[2].MaxWidth = 420;
            Grid.SetRow(HostSetupColumn, 0);
            Grid.SetColumn(HostSetupColumn, 0);
            Grid.SetColumnSpan(HostSetupColumn, 1);
            Grid.SetRow(HostShareCard, 0);
            Grid.SetColumn(HostShareCard, 2);
            Grid.SetColumnSpan(HostShareCard, 1);
            HostShareCard.Margin = new Thickness(0);
        }
    }

    internal void VerifyResponsiveLayout()
    {
        if (!IsLoaded) throw new InvalidOperationException("The WPF window did not load.");
        var work = SystemParameters.WorkArea;
        const double wideViewportWidth = 1180;
        var wideWindowWidth = Math.Max(MinWidth, Math.Min(wideViewportWidth, work.Width - 32));
        var wideHeight = Math.Max(MinHeight, 800);
        Pages.SelectedItem = ServerTab;
        Width = wideWindowWidth;
        Height = wideHeight;
        Left = work.Left;
        Top = work.Top;
        UpdateLayout();
        UpdateHostLayout(wideViewportWidth);
        UpdateLayout();
        if (WindowLayout.StackHostPanels(wideViewportWidth) || Grid.GetRow(HostShareCard) != 0 || Grid.GetColumn(HostShareCard) != 2 || Grid.GetColumnSpan(HostShareCard) != 1)
            throw new InvalidOperationException("The host page did not keep its wide two-column layout.");
        if (HostContentGrid.ActualWidth <= 0 || HostContentGrid.ActualWidth > ActualWidth + 1)
            throw new InvalidOperationException("The host setup cards exceed the WPF window width.");
        if (HostSetupColumn.ActualWidth <= 0 || HostSetupColumn.ActualWidth > HostContentGrid.ActualWidth + 1 || HostShareCard.ActualWidth <= 0 || HostShareCard.ActualWidth > HostContentGrid.ActualWidth + 1)
            throw new InvalidOperationException("A host card is clipped in the wide layout.");

        var narrowWidth = Math.Max(MinWidth, Math.Min(850, work.Width - 32));
        Width = narrowWidth;
        Height = Math.Max(MinHeight, Math.Min(620, work.Height - 32));
        UpdateLayout();
        UpdateHostLayout(narrowWidth);
        UpdateLayout();
        if (!WindowLayout.StackHostPanels(narrowWidth) || Grid.GetRow(HostShareCard) != 1 || Grid.GetColumnSpan(HostShareCard) != 3)
            throw new InvalidOperationException("The host page did not switch to the narrow layout.");
        if (HostShareCard.ActualWidth <= 0 || HostShareCard.ActualWidth > HostContentGrid.ActualWidth + 1)
            throw new InvalidOperationException("The share card is clipped in the narrow layout.");

        Pages.SelectedItem = ClientTab;
        UpdateLayout();
        if (ConnectionControls.ActualWidth <= 0 || ConnectionControls.ActualWidth > ClientHost.ActualWidth + 1)
            throw new InvalidOperationException("The connection card is clipped in the narrow layout.");
    }

    private void EnsureTray()
    {
        if (tray != null) return;
        System.Drawing.Icon icon;
        try
        {
            trayIcon = string.IsNullOrWhiteSpace(Environment.ProcessPath) ? null : System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            icon = trayIcon ?? System.Drawing.SystemIcons.Application;
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or System.ComponentModel.Win32Exception)
        {
            icon = System.Drawing.SystemIcons.Application;
        }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var open = new System.Windows.Forms.ToolStripMenuItem(UiText.TrayOpen());
        open.Click += (_, _) => BringToFront();
        var exit = new System.Windows.Forms.ToolStripMenuItem(UiText.TrayExit());
        exit.Click += (_, _) => AppLifetime.RequestExit();
        menu.Items.Add(open);
        menu.Items.Add(exit);
        tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = UiText.TrayTip(false),
            ContextMenuStrip = menu
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) BringToFront();
        };
    }

    internal void NoteTray()
    {
        if (tray == null) return;
        var tip = UiText.TrayTip(hostSession.Listening);
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        if (trayExplained || IsVisible) return;
        trayExplained = true;
        tray.ShowBalloonTip(2500, productTitle, UiText.TrayHidden(hostSession.Listening), System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ReleaseTray()
    {
        if (tray != null)
        {
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }
        trayIcon?.Dispose();
        trayIcon = null;
    }

    private bool ConfirmClose()
    {
        var prompt = UiText.ClosePrompt(clientSession.RemoteEndpoint, hostSession.ConnectedPeer);
        if (prompt == null) return true;
        return MessageBox.Show(clientSession.DialogOwner(this), prompt, productTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void HostStatus(string text) => Dispatcher.InvokeAsync(() =>
    {
        ServerStatusText.Text = text;
        ServerStatusText.Foreground = UiText.IsProblem(text)
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("InkBrush");
        KickButton.IsEnabled = hostSession.HasClient && StopButton.IsEnabled;
        KickButton.Visibility = KickButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        hostSession.SyncConnectedClock();
        RefreshIdentity();
    });

    private void RefreshIdentity()
    {
        var inbound = hostSession.ConnectedPeer;
        var outbound = clientSession.RemoteEndpoint;
        var newlyConnected = !string.IsNullOrEmpty(inbound) && inbound != attentionPeer;
        var departed = string.IsNullOrEmpty(inbound) && !string.IsNullOrEmpty(attentionPeer);
        attentionPeer = string.IsNullOrEmpty(inbound) ? null : inbound;
        ServerTab.Header = UiText.HostTabLabel(inbound);
        Title = UiText.WindowTitle(productTitle, outbound, inbound);
        clientSession.SetFullscreenTitle(UiText.FullscreenTitle(outbound, inbound));
        SessionReveal.ToolTip = UiText.RevealTip(clientSession.ViewingLabel, inbound);
        if (departed) StopAttention();
        else if (UiText.NeedsAttention(newlyConnected, IsActive || clientSession.IsFullscreenActive)) StartAttention();
        var hostPageVisible = UiText.HostPageVisible(ServerTab.IsSelected, clientSession.IsFullscreen);
        if (UiText.AnnounceInbound(newlyConnected, hostPageVisible))
            clientSession.AnnounceInbound(inbound);
        if (departed && !hostPageVisible && UiText.HostSessionEnded(ServerStatusText.Text))
        {
            if (clientSession.RemoteEndpoint != null) clientSession.AnnounceHostNotice(ServerStatusText.Text);
            else ClientNotice(ServerStatusText.Text);
        }
    }

    private void StartAttention()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var info = new FlashInfo { cbSize = (uint)Marshal.SizeOf<FlashInfo>(), hwnd = hwnd, flags = 0xF, count = uint.MaxValue };
        FlashWindowEx(ref info);
    }

    private void StopAttention()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        var info = new FlashInfo { cbSize = (uint)Marshal.SizeOf<FlashInfo>(), hwnd = hwnd };
        FlashWindowEx(ref info);
    }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint cbSize; public nint hwnd; public uint flags; public uint count; public uint timeout; }

    private void HostNotice(string text, bool problem = false) => Dispatcher.InvokeAsync(() => hostSession.ShowNotice(text, problem));

    private void SessionStatus(string text) => Dispatcher.InvokeAsync(() =>
    {
        StatusText.Text = text;
        var problem = UiText.IsProblem(text);
        StatusText.Foreground = problem
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("InkBrush");
        SessionAlertText.Text = text;
        clientSession.NoteSessionStatus(problem);
    });

    private void ClientNotice(string text, bool problem = false) => Dispatcher.InvokeAsync(() =>
    {
        ClientNoticeText.Text = text;
        ClientNoticeText.Foreground = problem
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    });

    private void SmoothHostChanged(object sender, RoutedEventArgs e) => hostSession.SmoothHostChanged(sender, e);
    private void ChangeHostPassword(object sender, RoutedEventArgs e) => hostSession.ChangeHostPassword(sender, e);
    private void SaveHostPassword(object sender, RoutedEventArgs e) => hostSession.SaveHostPassword(sender, e);
    private void HostPasswordKeyDown(object sender, KeyEventArgs e) => hostSession.HostPasswordKeyDown(sender, e);
    private void HostPasswordRevealChanged(object sender, RoutedEventArgs e) { if (initializing) return; hostSession.HostPasswordRevealChanged(sender, e); }
    private void HostPasswordVisibleChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (initializing) return; hostSession.HostPasswordVisibleChanged(sender, e); }
    private void HostPasswordConfirmVisibleChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (initializing) return; hostSession.HostPasswordConfirmVisibleChanged(sender, e); }
    private void StartHost(object sender, RoutedEventArgs e) => hostSession.StartHost(sender, e);
    private void StopHost(object sender, RoutedEventArgs e) => hostSession.StopHost(sender, e);
    private void Kick(object sender, RoutedEventArgs e) => hostSession.Kick(sender, e);
    private void InstallForUser(object sender, RoutedEventArgs e) => hostSession.InstallForUser(sender, e);
    private void AutostartChanged(object sender, RoutedEventArgs e) { if (initializing) return; hostSession.AutostartChanged(sender, e); }
    private void InstallForMachine(object sender, RoutedEventArgs e) => hostSession.InstallForMachine(sender, e);
    private void UninstallApp(object sender, RoutedEventArgs e) => hostSession.UninstallApp(sender, e);
    private void CopyFingerprint(object sender, RoutedEventArgs e) => hostSession.CopyFingerprint(sender, e);
    private void ToggleFingerprint(object sender, RoutedEventArgs e) => hostSession.ToggleFingerprint(sender, e);
    private void CopyAddress(object sender, RoutedEventArgs e) => hostSession.CopyAddress(sender, e);

    private void RefreshCapsLock(object sender, RoutedEventArgs e)
    {
        if (e.RoutedEvent == Keyboard.GotKeyboardFocusEvent) SelectFocusedValue(sender);
        RefreshCapsLock();
    }

    private void RefreshCapsLock()
    {
        var caps = Keyboard.IsKeyToggled(Key.CapsLock);
        var clientFocused = ClientPassword.IsKeyboardFocused || ClientPasswordVisible.IsKeyboardFocused;
        var hostFocused = HostPassword.IsKeyboardFocused || HostPasswordVisible.IsKeyboardFocused;
        hostFocused = hostFocused || HostPasswordConfirm.IsKeyboardFocused || HostPasswordConfirmVisible.IsKeyboardFocused;
        ClientCapsLock.Visibility = UiText.ShowCapsLock(clientFocused, caps) ? Visibility.Visible : Visibility.Collapsed;
        HostCapsLock.Visibility = UiText.ShowCapsLock(hostFocused, caps) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectWholeValue(object sender, MouseButtonEventArgs e)
    {
        if (!UiText.SelectEntireValue(ValueAlreadyFocused(sender))) return;
        SelectFocusedValue(sender);
        e.Handled = true;
    }

    private void SelectWholeValueOnFocus(object sender, KeyboardFocusChangedEventArgs e) => SelectFocusedValue(sender);

    private static bool ValueAlreadyFocused(object sender) => sender switch
    {
        TextBox box => box.IsKeyboardFocusWithin,
        PasswordBox password => password.IsKeyboardFocusWithin,
        _ => true
    };

    private static void SelectFocusedValue(object sender)
    {
        switch (sender)
        {
            case TextBox box:
                box.Focus();
                box.SelectAll();
                break;
            case PasswordBox password:
                password.Focus();
                password.SelectAll();
                break;
        }
    }

    private void Connect(object sender, RoutedEventArgs e) => clientSession.Connect(sender, e);
    private void CancelReconnect(object sender, RoutedEventArgs e) => clientSession.CancelReconnect(sender, e);
    private void ClientPasswordKeyDown(object sender, KeyEventArgs e) => clientSession.ClientPasswordKeyDown(sender, e);
    private void ClientPasswordRevealChanged(object sender, RoutedEventArgs e) { if (initializing) return; clientSession.ClientPasswordRevealChanged(sender, e); }
    private void ClientPasswordVisibleChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (initializing) return; clientSession.ClientPasswordVisibleChanged(sender, e); }
    private void UseRecent(object sender, RoutedEventArgs e) => clientSession.UseRecent(sender, e);
    private void ForgetRecent(object sender, RoutedEventArgs e) => clientSession.ForgetRecent(sender, e);
    private void EditPin(object sender, RoutedEventArgs e) => clientSession.EditPin(sender, e);
    private void ClearPin(object sender, RoutedEventArgs e) => clientSession.ClearPin(sender, e);
    private void TabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (initializing) return;
        clientSession.TabChanged(e);
    }
    private void Disconnect(object sender, RoutedEventArgs e) => clientSession.Disconnect(sender, e);
    private void Move(object sender, MouseEventArgs e) => clientSession.Move(sender, e);
    private void PointerLeft(object sender, MouseEventArgs e) => clientSession.PointerLeft(sender, e);
    private void FileDrag(object sender, DragEventArgs e) => clientSession.FileDrag(sender, e);
    private void FileDragLeave(object sender, DragEventArgs e) => clientSession.FileDragLeave(sender, e);
    private void FileDrop(object sender, DragEventArgs e) => clientSession.FileDrop(sender, e);
    private void MouseDownRemote(object sender, MouseButtonEventArgs e) => clientSession.MouseDownRemote(sender, e);
    private void MouseUpRemote(object sender, MouseButtonEventArgs e) => clientSession.MouseUpRemote(sender, e);
    private void Wheel(object sender, MouseWheelEventArgs e) => clientSession.Wheel(sender, e);
    private void KeyDownRemote(object sender, KeyEventArgs e) => clientSession.KeyDownRemote(sender, e);
    private void KeyUpRemote(object sender, KeyEventArgs e) => clientSession.KeyUpRemote(sender, e);
    private void ToggleKorean(object sender, RoutedEventArgs e) => clientSession.ToggleKorean(sender, e);
    private void SendSecureAttention(object sender, RoutedEventArgs e) => clientSession.SendSecureAttention(sender, e);
    private void InputControlChanged(object sender, RoutedEventArgs e) { if (initializing) return; clientSession.InputControlChanged(sender, e); }
    private void ClipboardAutoChanged(object sender, RoutedEventArgs e) { if (initializing) return; clientSession.ClipboardAutoChanged(sender, e); }
    private void ClipboardSend(object sender, RoutedEventArgs e) => clientSession.ClipboardSend(sender, e);
    private void CancelTransfer(object sender, RoutedEventArgs e) => clientSession.CancelTransfer(sender, e);
    private void ClipboardReceive(object sender, RoutedEventArgs e) => clientSession.ClipboardReceive(sender, e);
    private void QualityMenuChanged(object sender, RoutedEventArgs e) => clientSession.QualityMenuChanged(sender, e);
    private void FrameRateMenuChanged(object sender, RoutedEventArgs e) => clientSession.FrameRateMenuChanged(sender, e);
    private void ZoomMenuChanged(object sender, RoutedEventArgs e) => clientSession.ZoomMenuChanged(sender, e);
    private void ShowSessionInfo(object sender, RoutedEventArgs e) => clientSession.ShowSessionInfo(sender, e);
    private void ShowHostPage(object sender, RoutedEventArgs e) => clientSession.ShowHostPage(sender, e);
    private void CloseSessionInfo(object sender, RoutedEventArgs e) => clientSession.CloseSessionInfo(sender, e);
    private void RemoteFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => clientSession.RemoteFocusChanged(sender, e);
    private void OpenSessionMenu(object sender, RoutedEventArgs e) => clientSession.OpenSessionMenu(sender, e);
    private void RevealSessionToolbar(object sender, RoutedEventArgs e) => clientSession.RevealSessionToolbar(sender, e);
    private void SessionToolbarActivity(object sender, System.Windows.Input.MouseEventArgs e) => clientSession.SessionToolbarActivity(sender, e);
    private void SessionMenuClosed(object sender, RoutedEventArgs e) => clientSession.SessionMenuClosed(sender, e);
    private void DisplayListClosed(object sender, EventArgs e) => clientSession.DisplayListClosed(sender, e);
    private void RefreshDisplays(object sender, RoutedEventArgs e) => clientSession.RefreshDisplays(sender, e);
    private void DisplayChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => clientSession.DisplayChanged(sender, e);
    private void ToggleFullscreen(object sender, RoutedEventArgs e) => clientSession.ToggleFullscreen(sender, e);
    private void ExitFullscreen(object sender, RoutedEventArgs e) => clientSession.ExitFullscreen();
    private void ReleaseInput(object sender, KeyboardFocusChangedEventArgs e) => clientSession.ReleaseInput(sender, e);
}
