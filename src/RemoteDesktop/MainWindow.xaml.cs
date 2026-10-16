using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RemoteDesktop;

public partial class MainWindow : Window
{
    private readonly HostSession hostSession;
    private readonly ClientSession clientSession;
    private readonly string productTitle;
    private string? attentionPeer;

    public MainWindow()
    {
        InitializeComponent();
        hostSession = new HostSession(this);
        clientSession = new ClientSession(this);
        HostPort.TextChanged += (_, _) => hostSession.RefreshAddresses();
        Address.TextChanged += (_, _) => clientSession.NormalizePastedAddress();
        ServerPin.TextChanged += (_, _) => clientSession.UpdatePinSummary();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        productTitle = version is null ? "Personal Remote Desktop" : "Personal Remote Desktop " + version.ToString(3);
        Title = productTitle;
        ServerTab.Visibility = Visibility.Visible;
        hostSession.Bind();
        clientSession.Bind();
        PreviewKeyDown += clientSession.FullscreenKey;
        SourceInitialized += (_, _) =>
        {
            clientSession.ApplyWindowPlacement();
            clientSession.InstallInputHooks();
        };
        Loaded += async (_, _) =>
        {
            await hostSession.StartIfConfigured();
            clientSession.FocusConnectField();
        };
        Closing += (_, _) =>
        {
            clientSession.SaveWindowPlacement();
            hostSession.Closing = true;
        };
        Activated += (_, _) => StopAttention();
        Closed += async (_, _) =>
        {
            clientSession.UninstallSystemShortcutHook();
            clientSession.CloseFullscreen();
            await clientSession.DisconnectClient();
            await hostSession.DisposeAsync();
        };
        Deactivated += (_, _) => clientSession.QueueRelease();
        Activated += (_, _) => clientSession.WindowActivationChanged();
    }

    internal void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void HostStatus(string text) => Dispatcher.InvokeAsync(() =>
    {
        ServerStatusText.Text = text;
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
        SessionReveal.ToolTip = string.IsNullOrEmpty(inbound) ? "세션 조작 열기" : "이 PC에 " + inbound + " 연결됨 · 세션 조작 열기";
        if (departed) StopAttention();
        else if (UiText.NeedsAttention(newlyConnected, IsActive || clientSession.IsFullscreenActive)) StartAttention();
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
    private void StartHost(object sender, RoutedEventArgs e) => hostSession.StartHost(sender, e);
    private void StopHost(object sender, RoutedEventArgs e) => hostSession.StopHost(sender, e);
    private void Kick(object sender, RoutedEventArgs e) => hostSession.Kick(sender, e);
    private void CopyFingerprint(object sender, RoutedEventArgs e) => hostSession.CopyFingerprint(sender, e);
    private void ToggleFingerprint(object sender, RoutedEventArgs e) => hostSession.ToggleFingerprint(sender, e);
    private void CopyAddress(object sender, RoutedEventArgs e) => hostSession.CopyAddress(sender, e);

    private void Connect(object sender, RoutedEventArgs e) => clientSession.Connect(sender, e);
    private void ClientPasswordKeyDown(object sender, KeyEventArgs e) => clientSession.ClientPasswordKeyDown(sender, e);
    private void UseRecent(object sender, RoutedEventArgs e) => clientSession.UseRecent(sender, e);
    private void ForgetRecent(object sender, RoutedEventArgs e) => clientSession.ForgetRecent(sender, e);
    private void EditPin(object sender, RoutedEventArgs e) => clientSession.EditPin(sender, e);
    private void ClearPin(object sender, RoutedEventArgs e) => clientSession.ClearPin(sender, e);
    private void TabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => clientSession.TabChanged(e);
    private void Disconnect(object sender, RoutedEventArgs e) => clientSession.Disconnect(sender, e);
    private void Move(object sender, MouseEventArgs e) => clientSession.Move(sender, e);
    private void MouseDownRemote(object sender, MouseButtonEventArgs e) => clientSession.MouseDownRemote(sender, e);
    private void MouseUpRemote(object sender, MouseButtonEventArgs e) => clientSession.MouseUpRemote(sender, e);
    private void Wheel(object sender, MouseWheelEventArgs e) => clientSession.Wheel(sender, e);
    private void KeyDownRemote(object sender, KeyEventArgs e) => clientSession.KeyDownRemote(sender, e);
    private void KeyUpRemote(object sender, KeyEventArgs e) => clientSession.KeyUpRemote(sender, e);
    private void ToggleKorean(object sender, RoutedEventArgs e) => clientSession.ToggleKorean(sender, e);
    private void InputControlChanged(object sender, RoutedEventArgs e) => clientSession.InputControlChanged(sender, e);
    private void ClipboardAutoChanged(object sender, RoutedEventArgs e) => clientSession.ClipboardAutoChanged(sender, e);
    private void ClipboardSend(object sender, RoutedEventArgs e) => clientSession.ClipboardSend(sender, e);
    private void ClipboardReceive(object sender, RoutedEventArgs e) => clientSession.ClipboardReceive(sender, e);
    private void QualityMenuChanged(object sender, RoutedEventArgs e) => clientSession.QualityMenuChanged(sender, e);
    private void FrameRateMenuChanged(object sender, RoutedEventArgs e) => clientSession.FrameRateMenuChanged(sender, e);
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
    private void ReleaseInput(object sender, KeyboardFocusChangedEventArgs e) => clientSession.ReleaseInput(sender, e);
}
