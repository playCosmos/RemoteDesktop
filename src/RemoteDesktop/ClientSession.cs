using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RemoteDesktop;

public partial class MainWindow
{
    private sealed class ClientSession
    {
        private readonly MainWindow window;
        private RemoteClient? client;
        private CancellationTokenSource? connection;
        private ClipboardSync? clipboardSync;
        private bool pasting;
        private bool suppressPasteKeyUp;
        private Channel<InputEvent>? inputs;
        private Channel<byte>? inputSignal;
        private readonly object inputGate = new();
        private readonly InputBacklog inputBacklog = new();
        private InputEvent? pendingMove;
        private InputEvent? pendingWheel;
        private readonly HashSet<int> pendingKeyDowns = [];
        private bool connecting;
        private bool connectedOnce;
        public string? RemoteEndpoint => client != null || holdingPicture ? lastConnection.Address : null;
        public bool IsFullscreenActive => fullscreen?.IsActive == true;
        public string? ViewingLabel => viewingSince == null ? null : UiText.ConnectedFor(DateTime.UtcNow - viewingSince.Value);
        private bool normalizingAddress;
        private bool suppressClientPasswordReveal;
        private long lastMove;
        private string? cursorHash;
        private BitmapSource? cursorImage;
        private int cursorHotX;
        private int cursorHotY;
        private bool cursorVisible;
        private Point? lastPointer;
        private DateTime? viewingSince;
        private string healthDetail = "연결 전";
        private System.Windows.Threading.DispatcherTimer? viewingClock;
        private int generation;
        private readonly ClientSettings lastConnection = ClientSettings.Load();
        private string? selectedDisplay;
        private string? renderedDisplay;
        private Window? fullscreen;
        private int unicodePackets;
        private bool inputEnabled = true;
        private int frameQuality;
        private int frameRate;
        private int changedSinceHealth;
        private DateTime healthWindowStarted = DateTime.UtcNow;
        private int heldSinceHealth;
        private bool haveFrameSample;
        private int sampleChanged;
        private double sampleSeconds = 1;
        private bool sampleHeld;
        private long lastFrameTicks;
        private long roundTripMilliseconds = -1;
        private double lastCaptureMilliseconds;
        private double lastEncodeMilliseconds;
        private bool lastFrameReused;
        private int lastFrameQuality;
        private string? announcedDelivery;
        private int lastFrameBytes;
        private WriteableBitmap? frameSurface;
        private nint systemShortcutHook;
        private KeyboardHookProc? systemShortcutHookProc;
        private readonly HashSet<int> suppressedSystemKeys = [];
        private bool remoteAltDown;
        private System.Windows.Threading.DispatcherTimer? toolbarTimer;
        private System.Windows.Threading.DispatcherTimer? sessionNoticeTimer;
        private string? settlingNotice;
        private volatile bool viewerWatching = true;
        private string? sessionPassword;
        private Exception? connectFailure;
        private int reconnectAttempt;
        private int resumeEpoch;
        private bool suppressReconnect;
        private bool holdingPicture;

        public ClientSession(MainWindow window) => this.window = window;

        public void Bind()
        {
            var endpoint = SplitEndpoint(lastConnection.Address);
            window.Address.Text = endpoint.Host;
            window.ClientPort.Text = endpoint.Port;
            window.ServerPin.Text = lastConnection.Fingerprint;
            window.ServerPin.Visibility = Visibility.Collapsed;
            UpdatePinSummary();
            frameQuality = Math.Clamp(lastConnection.FrameQuality, 25, 90);
            frameRate = lastConnection.FrameRate is 5 or 10 or 15 or 30 ? lastConnection.FrameRate : 15;
            window.ClipboardAutoMenu.IsChecked = lastConnection.ClipboardAutoSync;
            ShowSessionPreset();
            RefreshRecent();
            if (lastConnection.OpenHostTab) window.ServerTab.IsSelected = true;
            window.StateChanged += (_, _) => NoteViewerWatch();
            NoteViewerWatch();
            window.ScreenImage.SizeChanged += (_, _) => ApplyPictureScaling();
            window.ClientPasswordReveal.Content = UiText.PasswordReveal(false);
            window.ClientPasswordReveal.ToolTip = UiText.PasswordRevealTip(false);
        }

        public void TabChanged(System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!window.IsLoaded || e.Source is not System.Windows.Controls.TabControl) return;
            ApplySessionChrome();
            NoteViewerWatch();
            var tab = window.ServerTab.IsSelected ? "host" : "client";
            if (string.Equals(lastConnection.SelectedTab, tab, StringComparison.OrdinalIgnoreCase)) return;
            lastConnection.SelectedTab = tab;
            SaveConnection();
        }

        public void ShowHostPage(object sender, RoutedEventArgs e)
        {
            window.ServerTab.IsSelected = true;
            ApplySessionChrome();
        }

        private void ApplySessionChrome()
        {
            var edgeToEdge = UiText.EdgeToEdge(client != null, window.ClientTab.IsSelected, fullscreen != null);
            window.Pages.ApplyTemplate();
            if (window.Pages.Template?.FindName("HeaderBar", window.Pages) is Border header)
                header.Visibility = edgeToEdge ? Visibility.Collapsed : Visibility.Visible;
            if (fullscreen == null) window.ClientPanel.Margin = new Thickness(edgeToEdge ? 0 : 12);
            window.Stage.CornerRadius = new CornerRadius(edgeToEdge || fullscreen != null ? 0 : 12);
        }

        public void UseRecent(object sender, RoutedEventArgs e)
        {
            if (client != null || sender is not System.Windows.Controls.Button { Tag: RecentConnection recent }) return;
            if (EndpointText.TrySplit(recent.Address, out var host, out var port))
            {
                window.Address.Text = host;
                window.ClientPort.Text = port.ToString();
            }
            else window.Address.Text = recent.Address;
            window.ServerPin.Text = recent.Fingerprint;
            window.ServerPin.Visibility = Visibility.Collapsed;
            UpdatePinSummary();
            if (recent.FrameQuality is >= 25 and <= 90) frameQuality = recent.FrameQuality;
            if (recent.FrameRate is 5 or 10 or 15 or 30) frameRate = recent.FrameRate;
            if (!string.IsNullOrEmpty(recent.Display)) lastConnection.Display = recent.Display;
            ShowSessionPreset();
            FocusClientPassword();
            window.ClientNotice(recent.Address + " · 비밀번호를 입력하세요.");
        }

        public void ForgetRecent(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: RecentConnection recent }) return;
            if (!lastConnection.ForgetRecent(recent.Address)) return;
            SaveConnection();
            RefreshRecent();
            window.ClientNotice("최근 연결에서 제거했습니다.");
        }

        private void RefreshRecent()
        {
            var recent = lastConnection.Recent ?? [];
            window.RecentList.ItemsSource = recent.ToList();
            window.RecentPanel.Visibility = recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public void NormalizePastedAddress()
        {
            if (normalizingAddress || !EndpointText.TrySplit(window.Address.Text, out var host, out var port)) return;
            normalizingAddress = true;
            try
            {
                window.Address.Text = host;
                window.Address.CaretIndex = host.Length;
                window.ClientPort.Text = port.ToString();
            }
            finally { normalizingAddress = false; }
        }

        public void ApplyWindowPlacement()
        {
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (!lastConnection.TryGetWindowPlacement(screen.Left, screen.Top, screen.Width, screen.Height, out var placement)) return;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = placement.Left;
            window.Top = placement.Top;
            window.Width = placement.Width;
            window.Height = placement.Height;
            if (placement.Maximized) window.WindowState = WindowState.Maximized;
        }

        public void SaveWindowPlacement()
        {
            var bounds = window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
            lastConnection.RememberWindow(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized);
            SaveConnection();
        }

        public void InstallInputHooks()
        {
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(NativeKeyboard);
            InstallSystemShortcutHook();
        }

        public void UninstallSystemShortcutHook()
        {
            if (systemShortcutHook != 0) UnhookWindowsHookEx(systemShortcutHook);
            systemShortcutHook = 0;
            systemShortcutHookProc = null;
        }

        public void CloseFullscreen() => fullscreen?.Close();
        public Task DisconnectClient()
        {
            suppressReconnect = true;
            sessionPassword = null;
            resumeEpoch++;
            return DisconnectCore();
        }

        private async Task ForgetReconnectsAfterStable(CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
            reconnectAttempt = 0;
        }

        private async Task ResumeAfterDrop(Exception failure, int epoch)
        {
            while (!suppressReconnect && resumeEpoch == epoch && sessionPassword != null && client == null && !connecting)
            {
                var kind = UiText.ClassifyDrop(failure);
                if (!UiText.CanResume(kind) || reconnectAttempt >= UiText.MaxAutomaticReconnects)
                {
                    sessionPassword = null;
                    RevealConnectForm("연결 종료: " + UiText.ConnectionFailure(failure, true), problem: true);
                    return;
                }
                reconnectAttempt++;
                var wait = UiText.ReconnectDelay(reconnectAttempt);
                var notice = UiText.Reconnecting(reconnectAttempt, UiText.MaxAutomaticReconnects, (int)wait.TotalSeconds);
                window.ClientNotice(notice);
                ShowStage(notice);
                SetHealth(UiText.ReconnectHold());
                window.ConnectButton.IsEnabled = false;
                window.ConnectButton.Content = "다시 연결 중…";
                SetConnectFormEnabled(false);
                await Task.Delay(wait);
                if (suppressReconnect || resumeEpoch != epoch || sessionPassword == null || client != null || connecting) return;
                if (await ConnectCore(sessionPassword, announceFailure: false)) return;
                failure = connectFailure ?? failure;
            }
        }
        public void QueueRelease()
        {
            ReleaseSystemShortcuts();
            Queue(new("release"));
            UpdateInputChrome();
        }

        public void FocusConnectField()
        {
            if (!window.ClientTab.IsSelected || window.ConnectionControls.Visibility != Visibility.Visible) return;
            if (string.IsNullOrWhiteSpace(window.Address.Text)) window.Address.Focus();
            else FocusClientPassword();
        }

        public async void Connect(object sender, RoutedEventArgs e)
        {
            suppressReconnect = false;
            reconnectAttempt = 0;
            resumeEpoch++;
            await ConnectCore(CurrentClientPassword(), announceFailure: true);
        }

        private async Task<bool> ConnectCore(string password, bool announceFailure)
        {
            if (connecting || client != null) return false;
            connecting = true;
            window.ConnectButton.IsEnabled = false;
            window.ConnectButton.Content = "접속 중…";
            SetConnectFormEnabled(false);
            window.ClientNotice("접속하는 중…");
            var attempt = generation;
            try
            {
                var endpoint = BuildEndpoint();
                var pin = window.ServerPin.Text.Trim();
                if (pin.Length == 0)
                {
                    window.ClientNotice("비밀번호로 서버 키를 확인하는 중입니다…");
                    pin = await PakeBootstrap.AcquireServerFingerprint(endpoint, password);
                    window.ServerPin.Text = pin;
                }
                var remote = new RemoteClient(endpoint, pin);
                try { await remote.Login(password); } catch { remote.Dispose(); throw; }
                if (attempt != generation) { try { await remote.Logout(); } finally { remote.Dispose(); } return false; }
                client = remote;
                connectedOnce = true;
                sessionPassword = password;
                ClearClientPassword();
                var previousHost = lastConnection.Address;
                lastConnection.Address = endpoint;
                lastConnection.Fingerprint = pin;
                if (previousHost != lastConnection.Address) lastConnection.Display = "";
                lastConnection.RememberRecent(endpoint, pin, lastConnection.Display, frameQuality, frameRate);
                SaveConnection();
                RefreshRecent();
                try { await LoadDisplays(); } catch { await DisconnectCore(retrying: !announceFailure); throw; }
                connection = new();
                inputs = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = false });
                inputSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
                inputBacklog.Reset();
                lock (inputGate) { pendingMove = null; pendingWheel = null; pendingKeyDowns.Clear(); }
                clipboardSync = new ClipboardSync(remote, new WindowsFileClipboard(), window.SessionStatus);
                clipboardSync.AutoEnabled = window.ClipboardAutoMenu.IsChecked == true;
                inputEnabled = true;
                window.InputControlToggle.IsChecked = true;
                window.ConnectionControls.Visibility = Visibility.Collapsed;
                SetSessionControlsEnabled(true);
                ShowStage("화면을 받는 중…");
                changedSinceHealth = 0;
                healthWindowStarted = DateTime.UtcNow;
                roundTripMilliseconds = -1;
                window.SessionStatus("연결됨");
                if (window.IsActive) window.ScreenImage.Focus();
                UpdateInputChrome();
                window.RefreshIdentity();
                _ = RunConnection(remote, connection.Token);
                return true;
            }
            catch (Exception ex)
            {
                connectFailure = ex;
                if (announceFailure) window.ClientNotice("접속 실패: " + UiText.ConnectionFailure(ex, false), problem: true);
                return false;
            }
            finally
            {
                connecting = false;
                if (client != null || announceFailure)
                {
                    window.ConnectButton.IsEnabled = client == null;
                    if (client == null) SetConnectFormEnabled(true);
                    if (client == null) window.ConnectButton.Content = connectedOnce ? "다시 접속" : "접속";
                }
            }
        }

        private void SetConnectFormEnabled(bool enabled)
        {
            window.Address.IsEnabled = enabled;
            window.ClientPort.IsEnabled = enabled;
            window.ClientPassword.IsEnabled = enabled;
            window.ClientPasswordVisible.IsEnabled = enabled;
            window.ClientPasswordReveal.IsEnabled = enabled;
            window.ServerPin.IsEnabled = enabled;
            window.EditPinButton.IsEnabled = enabled;
            window.ClearPinButton.IsEnabled = enabled;
            window.RecentPanel.IsEnabled = enabled;
        }

        public void ClientPasswordKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                e.Handled = true;
                Connect(sender, e);
            }
        }

        public void ClientPasswordRevealChanged(object sender, RoutedEventArgs e)
        {
            if (suppressClientPasswordReveal) return;
            var shown = window.ClientPasswordReveal.IsChecked == true;
            window.ClientPasswordReveal.Content = UiText.PasswordReveal(shown);
            window.ClientPasswordReveal.ToolTip = UiText.PasswordRevealTip(shown);
            if (shown)
            {
                suppressClientPasswordReveal = true;
                window.ClientPasswordVisible.Text = window.ClientPassword.Password;
                suppressClientPasswordReveal = false;
                window.ClientPassword.Visibility = Visibility.Collapsed;
                window.ClientPasswordVisible.Visibility = Visibility.Visible;
                window.ClientPasswordVisible.CaretIndex = window.ClientPasswordVisible.Text.Length;
                if (window.ClientPasswordVisible.IsEnabled) window.ClientPasswordVisible.Focus();
            }
            else
            {
                window.ClientPassword.Password = window.ClientPasswordVisible.Text;
                suppressClientPasswordReveal = true;
                window.ClientPasswordVisible.Clear();
                suppressClientPasswordReveal = false;
                window.ClientPasswordVisible.Visibility = Visibility.Collapsed;
                window.ClientPassword.Visibility = Visibility.Visible;
                if (window.ClientPassword.IsEnabled) window.ClientPassword.Focus();
            }
        }

        public void ClientPasswordVisibleChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressClientPasswordReveal || window.ClientPasswordReveal.IsChecked != true) return;
            window.ClientPassword.Password = window.ClientPasswordVisible.Text;
        }

        private string CurrentClientPassword()
        {
            if (window.ClientPasswordReveal.IsChecked == true)
                window.ClientPassword.Password = window.ClientPasswordVisible.Text;
            return window.ClientPassword.Password;
        }

        private void FocusClientPassword()
        {
            if (window.ClientPasswordReveal.IsChecked == true) window.ClientPasswordVisible.Focus();
            else window.ClientPassword.Focus();
        }

        private void ClearClientPassword()
        {
            suppressClientPasswordReveal = true;
            window.ClientPassword.Clear();
            window.ClientPasswordVisible.Clear();
            window.ClientPasswordReveal.IsChecked = false;
            window.ClientPasswordReveal.Content = UiText.PasswordReveal(false);
            window.ClientPasswordReveal.ToolTip = UiText.PasswordRevealTip(false);
            window.ClientPasswordVisible.Visibility = Visibility.Collapsed;
            window.ClientPassword.Visibility = Visibility.Visible;
            suppressClientPasswordReveal = false;
        }

        private string BuildEndpoint()
        {
            var host = window.Address.Text.Trim();
            if (host.Length == 0) throw new ArgumentException("서버 주소를 입력하세요.");
            if (!int.TryParse(window.ClientPort.Text, out var port) || port is < 1 or > 65535) throw new ArgumentException("포트 범위: 1–65535");
            if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
            return host.Contains(':') ? "[" + host + "]:" + port : host + ":" + port;
        }

        private static (string Host, string Port) SplitEndpoint(string endpoint)
        {
            if (EndpointText.TrySplit(endpoint, out var host, out var port)) return (host, port.ToString());
            return (endpoint, "45831");
        }

        private async Task RunConnection(RemoteClient remote, CancellationToken ct)
        {
            _ = ForgetReconnectsAfterStable(ct);
            async Task Frames()
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!viewerWatching)
                    {
                        await Task.Delay(200, ct);
                        continue;
                    }
                    var started = Stopwatch.GetTimestamp();
                    var display = selectedDisplay;
                    byte[] data;
                    RemoteFrame frame;
                    try
                    {
                        var viewerHasFrame = renderedDisplay == display && window.ScreenImage.Source != null;
                        frame = await remote.FrameDetailed(ct, display, frameQuality, frameRate, inputEnabled, viewerHasFrame, cursorHash);
                        data = frame.Bytes;
                        lastCaptureMilliseconds = frame.CaptureMilliseconds;
                        lastEncodeMilliseconds = frame.EncodeMilliseconds;
                        lastFrameReused = frame.Reused;
                        var deliveredQuality = frame.Quality > 0 ? frame.Quality : frameQuality;
                        if (deliveredQuality != lastFrameQuality)
                        {
                            lastFrameQuality = deliveredQuality;
                            var snapshot = deliveredQuality;
                            _ = window.Dispatcher.InvokeAsync(() => AnnounceQuality(snapshot));
                        }
                        if (data.Length > 0) lastFrameBytes = data.Length;
                        var cursorFrame = frame;
                        _ = window.Dispatcher.InvokeAsync(() => ApplyRemoteCursor(cursorFrame));
                    }
                    catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
                    { await LoadDisplays(); continue; }
                    if (display != selectedDisplay) continue;
                    if (data.Length > 0) await PresentFrameAsync(data, generation);
                    if (display != selectedDisplay || ct.IsCancellationRequested) continue;
                    ShowStage(null);
                    renderedDisplay = display;
                    UpdateFrameHealth(data.Length > 0, frame.Reused && data.Length == 0);
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    var delay = RemoteClient.FrameInterval(frameRate, lastFrameReused) - elapsed;
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                }
            }
            async Task SendInputs()
            {
                var signal = inputSignal!;
                var channel = inputs!;
                while (await signal.Reader.WaitToReadAsync(ct))
                {
                    while (signal.Reader.TryRead(out _)) { }
                    while (channel.Reader.TryRead(out var input))
                    {
                        await remote.Input(input, ct);
                        if (input.Kind == "key" && input.Down) lock (inputGate) pendingKeyDowns.Remove(input.Code);
                    }
                    InputEvent? move, wheel;
                    lock (inputGate) { move = pendingMove; wheel = pendingWheel; pendingMove = null; pendingWheel = null; }
                    if (move != null) await remote.Input(move, ct);
                    if (wheel != null) await remote.Input(wheel, ct);
                    if (inputBacklog.Take())
                    {
                        lock (inputGate) pendingKeyDowns.Clear();
                        await remote.Input(new("release"), ct);
                    }
                }
            }
            async Task Heartbeat()
            {
                while (!ct.IsCancellationRequested)
                {
                    var timer = Stopwatch.StartNew();
                    await remote.Ping(ct);
                    timer.Stop();
                    Interlocked.Exchange(ref roundTripMilliseconds, timer.ElapsedMilliseconds);
                    UpdateHealthText();
                    await Task.Delay(3000, ct);
                }
            }
            var tasks = new[] { Frames(), SendInputs(), Heartbeat() };
            async Task SyncClipboard()
            {
                try { await clipboardSync!.Run(ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { window.SessionStatus("클립보드 동기화 오류: " + UiText.Explain(ex, "다시 시도하세요.")); }
            }
            var clipboardTask = SyncClipboard();
            Exception? failure = null;
            try { await await Task.WhenAny(tasks); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { failure = ex; }
            catch (OperationCanceledException) { }
            finally
            {
                var current = ReferenceEquals(client, remote);
                var retrying = current && failure != null && !suppressReconnect && sessionPassword != null && UiText.CanResume(UiText.ClassifyDrop(failure)) && reconnectAttempt < UiText.MaxAutomaticReconnects;
                if (current) await DisconnectCore(retrying);
                try { await Task.WhenAll(tasks); } catch { }
                await clipboardTask;
                if (retrying) await ResumeAfterDrop(failure!, resumeEpoch);
                else if (current && failure != null && !suppressReconnect)
                {
                    sessionPassword = null;
                    window.ClientNotice("연결 종료: " + UiText.ConnectionFailure(failure, true), problem: true);
                }
            }
        }

        private async Task DisconnectCore(bool retrying = false)
        {
            generation++;
            var remote = client;
            client = null;
            holdingPicture = retrying;
            if (!retrying) fullscreen?.Close();
            window.RefreshIdentity();
            connection?.Cancel();
            inputs?.Writer.TryComplete();
            inputSignal?.Writer.TryComplete();
            lock (inputGate) { pendingMove = null; pendingWheel = null; pendingKeyDowns.Clear(); }
            inputBacklog.Reset();
            if (!retrying) ClearFrame();
            ShowStage(retrying ? UiText.ReconnectHold() : null);
            window.SessionInfoCard.Visibility = Visibility.Collapsed;
            if (!retrying) selectedDisplay = null;
            SetSessionControlsEnabled(false);
            if (retrying)
            {
                window.ConnectionControls.Visibility = Visibility.Collapsed;
                window.SessionToolbar.Visibility = Visibility.Visible;
                window.SessionReveal.Visibility = Visibility.Collapsed;
                window.EndSessionButton.IsEnabled = true;
                SetHealth(UiText.ReconnectHold());
            }
            else
            {
                window.ConnectionControls.Visibility = Visibility.Visible;
                SetHealth("연결 종료");
            }
            SetConnectFormEnabled(!retrying);
            inputEnabled = false;
            window.ConnectButton.IsEnabled = !retrying;
            if (remote != null) window.ConnectButton.Content = retrying ? "다시 연결 중…" : "다시 접속";
            window.ServerPin.Visibility = Visibility.Collapsed;
            UpdatePinSummary();
            if (!retrying && window.IsVisible && window.ClientTab.IsSelected) FocusClientPassword();
            if (remote != null) { try { await remote.Logout(); } catch { } remote.Dispose(); }
        }

        private void RevealConnectForm(string notice, bool problem)
        {
            holdingPicture = false;
            fullscreen?.Close();
            ClearFrame();
            ShowStage(null);
            selectedDisplay = null;
            SetSessionControlsEnabled(false);
            window.ConnectionControls.Visibility = Visibility.Visible;
            SetConnectFormEnabled(true);
            window.ConnectButton.IsEnabled = true;
            window.ConnectButton.Content = connectedOnce ? "다시 접속" : "접속";
            SetHealth("연결 종료");
            window.RefreshIdentity();
            window.ClientNotice(notice, problem);
            if (window.IsVisible && window.ClientTab.IsSelected) FocusClientPassword();
        }

        public void UpdatePinSummary()
        {
            var pin = window.ServerPin.Text.Trim();
            var hasPin = pin.Length > 0;
            window.ClearPinButton.Visibility = hasPin ? Visibility.Visible : Visibility.Collapsed;
            window.EditPinButton.Content = hasPin ? "변경" : "지문 입력";
            window.PinSummary.Text = hasPin ? "저장된 지문 " + (pin.Length <= 12 ? pin : pin[..12] + "…") : "";
        }

        public void EditPin(object sender, RoutedEventArgs e)
        {
            window.ServerPin.Visibility = Visibility.Visible;
            window.ServerPin.Focus();
            window.ServerPin.CaretIndex = window.ServerPin.Text.Length;
        }

        public void ClearPin(object sender, RoutedEventArgs e)
        {
            window.ServerPin.Clear();
            window.ServerPin.Visibility = Visibility.Collapsed;
            UpdatePinSummary();
            window.ClientNotice("지문을 비우면 비밀번호로 서버 키를 확인합니다.");
        }

        public async void Disconnect(object sender, RoutedEventArgs e)
        {
            suppressReconnect = true;
            sessionPassword = null;
            resumeEpoch++;
            await DisconnectCore();
            window.ClientNotice("연결 종료됨");
        }

        private void SetSessionControlsEnabled(bool enabled)
        {
            window.SessionToolbar.Visibility = Visibility.Collapsed;
            window.SessionReveal.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            window.InputControlToggle.IsEnabled = enabled;
            window.DisplayList.IsEnabled = enabled;
            window.SessionMenuButton.IsEnabled = enabled;
            window.EndSessionButton.IsEnabled = enabled;
            ApplySessionChrome();
            if (enabled) StartViewingClock();
            else StopViewingClock();
            if (enabled) SetHealth("측정 중");
            if (enabled)
            {
                haveFrameSample = false;
                changedSinceHealth = 0;
                heldSinceHealth = 0;
                healthWindowStarted = DateTime.UtcNow;
                Interlocked.Exchange(ref lastFrameTicks, DateTime.UtcNow.Ticks);
            }
            ShowSessionPreset();
            if (!enabled) window.SessionAlert.Visibility = Visibility.Collapsed;
            if (!enabled) sessionNoticeTimer?.Stop();
            if (!enabled) settlingNotice = null;
            if (!enabled) { announcedDelivery = null; lastFrameQuality = 0; }
            UpdateInputChrome();
            if (enabled) ShowSessionToolbar();
            else toolbarTimer?.Stop();
        }

        public void NoteSessionStatus(bool problem)
        {
            if (client == null)
            {
                sessionNoticeTimer?.Stop();
                settlingNotice = null;
                window.SessionAlert.Visibility = Visibility.Collapsed;
                return;
            }
            ScheduleSessionBaseline();
            if (problem) ShowSessionToolbar();
            else SyncProblemAlert(window.SessionToolbar.Visibility == Visibility.Visible);
        }

        private void ScheduleSessionBaseline()
        {
            var text = window.StatusText.Text;
            if (text == UiText.SessionBaseline(inputEnabled) || UiText.SessionNoticeHolds(text))
            {
                sessionNoticeTimer?.Stop();
                settlingNotice = null;
                return;
            }
            settlingNotice = text;
            sessionNoticeTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            sessionNoticeTimer.Stop();
            sessionNoticeTimer.Tick -= RestoreSessionBaseline;
            sessionNoticeTimer.Tick += RestoreSessionBaseline;
            sessionNoticeTimer.Start();
        }

        private void RestoreSessionBaseline(object? sender, EventArgs e)
        {
            sessionNoticeTimer?.Stop();
            if (client == null || window.StatusText.Text != settlingNotice) return;
            settlingNotice = null;
            window.SessionStatus(UiText.SessionBaseline(inputEnabled));
        }

        private void SyncProblemAlert(bool toolbarOpen)
        {
            var problem = client != null && UiText.IsProblem(window.StatusText.Text);
            window.SessionAlert.Visibility = problem && !toolbarOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetHealth(string text)
        {
            healthDetail = text;
            var tone = (System.Windows.Media.Brush)window.FindResource(UiText.HealthTone(text));
            window.HealthText.Foreground = tone;
            window.SessionReveal.Foreground = tone;
            PublishHealth();
        }

        private void StartViewingClock()
        {
            viewingSince = DateTime.UtcNow;
            viewingClock ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            viewingClock.Stop();
            viewingClock.Tick -= PublishHealthTick;
            viewingClock.Tick += PublishHealthTick;
            viewingClock.Start();
        }

        private void StopViewingClock()
        {
            viewingClock?.Stop();
            viewingSince = null;
        }

        private void PublishHealthTick(object? sender, EventArgs e)
        {
            if (!viewerWatching)
            {
                if (client != null && healthDetail != UiText.PicturePaused()) SetHealth(UiText.PicturePaused());
                else PublishHealth();
                return;
            }
            if (client != null && SeenFrame() && UiText.FrameStale(DateTime.UtcNow - LastFrameUtc()) && healthDetail != UiText.StaleFrame())
                SetHealth(UiText.StaleFrame());
            else PublishHealth();
        }

        private void PublishHealth()
        {
            var clock = viewingSince == null ? null : UiText.ElapsedClock(DateTime.UtcNow - viewingSince.Value);
            var text = UiText.SessionHealth(clock, healthDetail);
            window.HealthText.Text = text;
            window.SessionReveal.Content = UiText.SessionReveal(text, inputEnabled);
            window.SessionReveal.ToolTip = UiText.RevealTip(ViewingLabel, window.hostSession.ConnectedPeer);
        }

        private void NoteViewerWatch()
        {
            var watching = UiText.ViewerWatching(fullscreen != null, fullscreen?.WindowState == WindowState.Minimized, window.WindowState == WindowState.Minimized, window.ClientTab.IsSelected);
            if (watching == viewerWatching) return;
            viewerWatching = watching;
            if (client == null) return;
            if (!watching) SetHealth(UiText.PicturePaused());
            else
            {
                if (SeenFrame()) Interlocked.Exchange(ref lastFrameTicks, DateTime.UtcNow.Ticks);
                UpdateHealthText();
            }
        }

        private void ShowSessionToolbar()
        {
            if (client == null) return;
            window.SessionToolbar.Visibility = Visibility.Visible;
            window.SessionReveal.Visibility = Visibility.Collapsed;
            SyncProblemAlert(true);
            RestartToolbarTimer();
        }

        private void RestartToolbarTimer()
        {
            toolbarTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
            toolbarTimer.Stop();
            toolbarTimer.Tick -= HideSessionToolbar;
            toolbarTimer.Tick += HideSessionToolbar;
            toolbarTimer.Start();
        }

        private void HideSessionToolbar(object? sender, EventArgs e)
        {
            toolbarTimer?.Stop();
            if (client == null) return;
            if (window.SessionMenu.IsOpen || window.DisplayList.IsDropDownOpen || window.SessionToolbar.IsMouseOver || window.SessionToolbar.IsKeyboardFocusWithin)
            {
                RestartToolbarTimer();
                return;
            }
            window.SessionToolbar.Visibility = Visibility.Collapsed;
            window.SessionReveal.Visibility = Visibility.Visible;
            SyncProblemAlert(false);
        }

        public void RevealSessionToolbar(object sender, RoutedEventArgs e) => ShowSessionToolbar();

        public void SessionToolbarActivity(object sender, MouseEventArgs e) => RestartToolbarTimer();

        public void SessionMenuClosed(object sender, RoutedEventArgs e) => RestartToolbarTimer();

        public void DisplayListClosed(object sender, EventArgs e) => RestartToolbarTimer();

        private void Queue(InputEvent input)
        {
            if (!inputEnabled && input.Kind != "release") return;
            var channel = inputs;
            var signal = inputSignal;
            if (client == null || channel == null || signal == null) return;
            var overflow = false;
            lock (inputGate)
            {
                if (input.Kind == "move") pendingMove = input;
                else if (input.Kind == "wheel") overflow = !TryCoalesceWheel(channel, input.Code);
                else if (input.Kind == "key" && input.Down && !pendingKeyDowns.Add(input.Code)) return;
                else overflow = !EnqueueDiscrete(channel, input);
                if (overflow && input.Kind == "key" && input.Down) pendingKeyDowns.Remove(input.Code);
            }
            if (overflow && inputBacklog.Note()) window.SessionStatus(UiText.InputBacklog());
            if (overflow) ShowSessionToolbar();
            signal.Writer.TryWrite(0);
        }

        private bool TryCoalesceWheel(Channel<InputEvent> channel, int delta)
        {
            var sum = (long)(pendingWheel?.Code ?? 0) + delta;
            if (sum is >= -InputEvent.MaxWheelDelta and <= InputEvent.MaxWheelDelta)
            {
                pendingWheel = sum == 0 ? null : new InputEvent("wheel", Code: (int)sum);
                return true;
            }
            if (pendingMove != null && !channel.Writer.TryWrite(pendingMove)) return false;
            pendingMove = null;
            pendingWheel = null;
            while (sum is < -InputEvent.MaxWheelDelta or > InputEvent.MaxWheelDelta)
            {
                var chunk = sum > 0 ? InputEvent.MaxWheelDelta : -InputEvent.MaxWheelDelta;
                if (!channel.Writer.TryWrite(new InputEvent("wheel", Code: chunk))) return false;
                sum -= chunk;
            }
            pendingWheel = sum == 0 ? null : new InputEvent("wheel", Code: (int)sum);
            return true;
        }

        private bool EnqueueDiscrete(Channel<InputEvent> channel, InputEvent input)
        {
            if (pendingMove != null && !channel.Writer.TryWrite(pendingMove)) return false;
            pendingMove = null;
            if (pendingWheel != null && !channel.Writer.TryWrite(pendingWheel)) return false;
            pendingWheel = null;
            if (input.Kind == "key" && !input.Down) pendingKeyDowns.Remove(input.Code);
            return channel.Writer.TryWrite(input);
        }

        private void UpdateFrameHealth(bool changed, bool held)
        {
            if (changed) changedSinceHealth++;
            else if (held) heldSinceHealth++;
            Interlocked.Exchange(ref lastFrameTicks, DateTime.UtcNow.Ticks);
            if (DateTime.UtcNow - healthWindowStarted < TimeSpan.FromSeconds(1)) return;
            var seconds = Math.Max(0.01, (DateTime.UtcNow - healthWindowStarted).TotalSeconds);
            var changedFrames = changedSinceHealth;
            var pictureHeld = changedFrames == 0 && heldSinceHealth > 0;
            changedSinceHealth = 0;
            heldSinceHealth = 0;
            healthWindowStarted = DateTime.UtcNow;
            var latency = Interlocked.Read(ref roundTripMilliseconds);
            var text = UiText.FrameHealth(changedFrames, seconds, latency, pictureHeld);
            window.Dispatcher.InvokeAsync(() =>
            {
                sampleChanged = changedFrames;
                sampleSeconds = seconds;
                sampleHeld = pictureHeld;
                haveFrameSample = true;
                if (client != null && viewerWatching) SetHealth(text);
            });
        }

        private void UpdateHealthText()
        {
            var latency = Interlocked.Read(ref roundTripMilliseconds);
            window.Dispatcher.InvokeAsync(() =>
            {
                if (client == null) return;
                if (!viewerWatching) { SetHealth(UiText.PicturePaused()); return; }
                if (SeenFrame() && UiText.FrameStale(DateTime.UtcNow - LastFrameUtc()))
                {
                    SetHealth(UiText.StaleFrame());
                    return;
                }
                SetHealth(haveFrameSample
                    ? UiText.FrameHealth(sampleChanged, sampleSeconds, latency, sampleHeld)
                    : UiText.FrameHealth(0, 1, latency, false));
            });
        }

        private bool SeenFrame() => Interlocked.Read(ref lastFrameTicks) != 0;
        private DateTime LastFrameUtc() => new(Interlocked.Read(ref lastFrameTicks), DateTimeKind.Utc);

        private void ShowStage(string? hint)
        {
            window.StageHint.Text = hint ?? "";
            window.StageHint.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowSessionPreset()
        {
            var delivered = client != null && lastFrameQuality > 0 ? QualityLabel(lastFrameQuality) : null;
            window.SessionPresetText.Text = UiText.SessionPreset(client == null ? null : lastConnection.Address, QualityLabel(frameQuality), frameRate, delivered);
        }

        private void AnnounceQuality(int quality)
        {
            if (client == null || quality <= 0) return;
            lastFrameQuality = quality;
            var requested = QualityLabel(frameQuality);
            var delivered = QualityLabel(quality);
            var notice = UiText.QualityShift(requested, announcedDelivery, delivered);
            announcedDelivery = delivered;
            ShowSessionPreset();
            if (notice != null) window.SessionStatus(notice);
        }

        private static string QualityLabel(int quality) => quality <= 40 ? "저대역폭" : quality >= 70 ? "고화질" : "균형";

        private readonly record struct DecodedFrame(int Width, int Height, double DpiX, double DpiY, int Stride, byte[] Pixels);

        private static DecodedFrame DecodeFrame(byte[] jpeg)
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            using var bitmap = new System.Drawing.Bitmap(stream);
            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width <= 0 || height <= 0) throw new InvalidOperationException("빈 화면 프레임");
            System.Drawing.Imaging.BitmapData? bits = null;
            byte[]? pixels = null;
            try
            {
                bits = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var stride = bits.Stride;
                if (stride <= 0) throw new InvalidOperationException("화면 프레임 방향이 올바르지 않습니다.");
                var count = stride * height;
                pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
                Marshal.Copy(bits.Scan0, pixels, 0, count);
                var dpiX = bitmap.HorizontalResolution;
                var dpiY = bitmap.VerticalResolution;
                if (dpiX < 1 || !double.IsFinite(dpiX)) dpiX = 96;
                if (dpiY < 1 || !double.IsFinite(dpiY)) dpiY = 96;
                var owned = pixels;
                pixels = null;
                return new DecodedFrame(width, height, dpiX, dpiY, stride, owned);
            }
            finally
            {
                if (pixels != null) System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
                if (bits != null) bitmap.UnlockBits(bits);
            }
        }

        private async Task PresentFrameAsync(byte[] data, int attempt)
        {
            var decoded = await Task.Run(() => DecodeFrame(data));
            try
            {
                if (attempt != generation || client == null) return;
                if (frameSurface == null || frameSurface.PixelWidth != decoded.Width || frameSurface.PixelHeight != decoded.Height)
                {
                    frameSurface = new WriteableBitmap(decoded.Width, decoded.Height, decoded.DpiX, decoded.DpiY, System.Windows.Media.PixelFormats.Bgra32, null);
                    window.ScreenImage.Source = frameSurface;
                }
                frameSurface.WritePixels(new Int32Rect(0, 0, decoded.Width, decoded.Height), decoded.Pixels, decoded.Stride, 0);
                ApplyPictureScaling();
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(decoded.Pixels); }
        }

        private void ApplyPictureScaling()
        {
            if (window.ScreenImage.Source is not BitmapSource image || image.PixelWidth <= 0 || image.PixelHeight <= 0) return;
            var dpi = VisualTreeHelper.GetDpi(window.ScreenImage);
            var width = window.ScreenImage.ActualWidth * dpi.DpiScaleX;
            var height = window.ScreenImage.ActualHeight * dpi.DpiScaleY;
            if (width <= 0 || height <= 0) return;
            var scale = Math.Min(width / image.PixelWidth, height / image.PixelHeight);
            var mode = UiText.CrispPicture(scale) ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear;
            RenderOptions.SetBitmapScalingMode(window.ScreenImage, mode);
        }

        private void ClearFrame()
        {
            frameSurface = null;
            window.ScreenImage.Source = null;
            ClearRemoteCursor();
        }

        private void ApplyRemoteCursor(RemoteFrame frame)
        {
            if (!frame.LocalCursor)
            {
                ClearRemoteCursor();
                return;
            }
            cursorVisible = frame.CursorVisible;
            if (frame.CursorPng is { Length: > 8 } png && frame.CursorHash.Length > 0 && frame.CursorHash != cursorHash)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(png);
                image.EndInit();
                image.Freeze();
                cursorImage = image;
                cursorHash = frame.CursorHash;
            }
            if (frame.CursorHash == cursorHash)
            {
                cursorHotX = frame.CursorHotX;
                cursorHotY = frame.CursorHotY;
            }
            PlaceRemoteCursor();
        }

        private void PlaceRemoteCursor()
        {
            if (!cursorVisible || !inputEnabled || cursorImage == null || lastPointer == null || window.ScreenImage.Source is not BitmapSource picture)
            {
                window.RemoteCursor.Visibility = Visibility.Collapsed;
                if (window.ScreenImage.Cursor == Cursors.None) window.ScreenImage.Cursor = Cursors.Arrow;
                return;
            }
            var mark = CursorPlacement.Place(lastPointer.Value.X, lastPointer.Value.Y, cursorHotX, cursorHotY, cursorImage.PixelWidth, cursorImage.PixelHeight, picture.PixelWidth, picture.PixelHeight, window.ScreenImage.ActualWidth, window.ScreenImage.ActualHeight);
            if (!mark.Shown || !inputEnabled)
            {
                window.RemoteCursor.Visibility = Visibility.Collapsed;
                window.ScreenImage.Cursor = Cursors.Arrow;
                return;
            }
            window.RemoteCursor.Source = cursorImage;
            window.RemoteCursor.Width = mark.Width;
            window.RemoteCursor.Height = mark.Height;
            window.RemoteCursor.Margin = new Thickness(mark.Left, mark.Top, 0, 0);
            window.RemoteCursor.Visibility = Visibility.Visible;
            window.ScreenImage.Cursor = Cursors.None;
        }

        private void ClearRemoteCursor()
        {
            cursorImage = null;
            cursorHash = null;
            cursorVisible = false;
            lastPointer = null;
            window.RemoteCursor.Source = null;
            window.RemoteCursor.Visibility = Visibility.Collapsed;
            window.ScreenImage.Cursor = Cursors.Arrow;
        }

        public void PointerLeft(object sender, MouseEventArgs e)
        {
            lastPointer = null;
            PlaceRemoteCursor();
        }

        private bool Position(MouseEventArgs e)
        {
            if (pasting) return false;
            if (window.ScreenImage.Source is not BitmapSource image) return false;
            var scale = Math.Min(window.ScreenImage.ActualWidth / image.PixelWidth, window.ScreenImage.ActualHeight / image.PixelHeight);
            var w = image.PixelWidth * scale;
            var h = image.PixelHeight * scale;
            var p = e.GetPosition(window.ScreenImage);
            var x = (p.X - (window.ScreenImage.ActualWidth - w) / 2) / w;
            var y = (p.Y - (window.ScreenImage.ActualHeight - h) / 2) / h;
            if (!window.ScreenImage.IsMouseCaptured && (x < 0 || x > 1 || y < 0 || y > 1)) return false;
            Queue(new("move", Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), Display: renderedDisplay));
            return true;
        }

        public void Move(object sender, MouseEventArgs e)
        {
            var stageY = e.GetPosition(window.Stage).Y;
            if (client != null && stageY <= 20) ShowSessionToolbar();
            lastPointer = e.GetPosition(window.ScreenImage);
            PlaceRemoteCursor();
            if (Environment.TickCount64 - lastMove < 16) return;
            lastMove = Environment.TickCount64;
            if (client != null && !UiText.RemotePointer(stageY, window.ScreenImage.IsMouseCaptured)) return;
            Position(e);
        }

        public void MouseDownRemote(object sender, MouseButtonEventArgs e)
        {
            window.ScreenImage.Focus();
            if (client != null && !UiText.RemotePointer(e.GetPosition(window.Stage).Y, window.ScreenImage.IsMouseCaptured))
            {
                ShowSessionToolbar();
                e.Handled = true;
                return;
            }
            if (!Position(e)) return;
            window.ScreenImage.CaptureMouse();
            Queue(new("button", Code: Button(e.ChangedButton), Down: true));
            e.Handled = true;
        }

        public void MouseUpRemote(object sender, MouseButtonEventArgs e)
        {
            if (!UiText.RemoteButtonRelease(window.ScreenImage.IsMouseCaptured))
            {
                e.Handled = true;
                return;
            }
            Position(e);
            Queue(new("button", Code: Button(e.ChangedButton)));
            window.ScreenImage.ReleaseMouseCapture();
            e.Handled = true;
        }

        private static int Button(MouseButton button) => button == MouseButton.Right ? 1 : button == MouseButton.Middle ? 2 : 0;

        public void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (client != null && !UiText.RemotePointer(e.GetPosition(window.Stage).Y, window.ScreenImage.IsMouseCaptured))
            {
                e.Handled = true;
                return;
            }
            Queue(new("wheel", Code: e.Delta));
            e.Handled = true;
        }

        public void KeyDownRemote(object sender, KeyEventArgs e)
        {
            var code = NativeKey(e);
            if (code > 0) Queue(new("key", Code: code, Down: true));
            e.Handled = true;
        }

        private static int NativeKey(KeyEventArgs e) => KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);

        private void InstallSystemShortcutHook()
        {
            if (systemShortcutHook != 0) return;
            systemShortcutHookProc = SystemShortcutHook;
            systemShortcutHook = SetWindowsHookEx(13, systemShortcutHookProc, GetModuleHandle(null), 0);
        }

        private nint SystemShortcutHook(int code, nint message, nint data)
        {
            if (code < 0 || !RemoteInputFocused())
            {
                if (!RemoteInputFocused()) ReleaseSystemShortcuts();
                return CallNextHookEx(systemShortcutHook, code, message, data);
            }
            var keyboard = Marshal.PtrToStructure<KeyboardHookData>(data);
            var key = (int)keyboard.VirtualKey;
            var down = message is 0x0100 or 0x0104;
            var up = message is 0x0101 or 0x0105;
            if (!down && !up) return CallNextHookEx(systemShortcutHook, code, message, data);
            lock (suppressedSystemKeys)
            {
                if (!down)
                {
                    if (!suppressedSystemKeys.Remove(key)) return CallNextHookEx(systemShortcutHook, code, message, data);
                    if (key == 0x12) remoteAltDown = false;
                    Queue(new("key", Code: key));
                    return 1;
                }
                var winHeld = suppressedSystemKeys.Contains(0x5B) || suppressedSystemKeys.Contains(0x5C);
                var capture = UiText.CaptureSystemShortcut(key, true, (keyboard.Flags & 0x20) != 0, (GetAsyncKeyState(0x11) & 0x8000) != 0, remoteAltDown, winHeld, false);
                if (!capture) return CallNextHookEx(systemShortcutHook, code, message, data);
                if (key == 0x12) remoteAltDown = true;
                suppressedSystemKeys.Add(key);
                Queue(new("key", Code: key, Down: true));
                return 1;
            }
        }

        private void ReleaseSystemShortcuts()
        {
            lock (suppressedSystemKeys)
            {
                suppressedSystemKeys.Clear();
                remoteAltDown = false;
            }
        }

        private bool RemoteInputFocused() => client != null && inputEnabled && window.ScreenImage.IsKeyboardFocused && (window.IsActive || fullscreen?.IsActive == true);

        private nint NativeKeyboard(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (client == null || !window.ScreenImage.IsKeyboardFocused) return 0;
            if (message is 0x100 or 0x101 or 0x104 or 0x105)
            {
                var code = (int)wParam;
                if (code == 0xE5) code = (int)ImmGetVirtualKey(hwnd);
                var down = message is 0x100 or 0x104;
                if (code == 0x7A) { handled = true; if (down && ((long)lParam & (1L << 30)) == 0) ToggleFullscreen(window, new RoutedEventArgs()); return 0; }
                if (code == 0x56 && !down && suppressPasteKeyUp) { suppressPasteKeyUp = false; handled = true; return 0; }
                if (code == 0x56 && down && (GetAsyncKeyState(0x11) & 0x8000) != 0)
                { handled = true; suppressPasteKeyUp = true; if (!pasting) PasteRemoteClipboard(); return 0; }
                if (pasting) { handled = true; return 0; }
                if (code == 0xE7) { if (down) unicodePackets++; handled = true; return 0; }
                if (code is > 0 and < 255) { Queue(new("key", Code: code, Down: down)); handled = true; }
            }
            else if (message == 0x102)
            {
                if (unicodePackets > 0) { unicodePackets--; Queue(new("text", Text: ((char)wParam).ToString())); }
                handled = true;
            }
            return 0;
        }

        [DllImport("imm32.dll")] private static extern uint ImmGetVirtualKey(nint hwnd);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int idHook, KeyboardHookProc callback, nint module, uint threadId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
        private delegate nint KeyboardHookProc(int code, nint message, nint data);
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardHookData { public uint VirtualKey, ScanCode, Flags, Time; public nuint ExtraInfo; }

        private async void PasteRemoteClipboard()
        {
            if (client is not { } remote || clipboardSync is not { } sync || connection is null) return;
            var ct = connection.Token;
            pasting = true;
            Queue(new("release"));
            try
            {
                await sync.PushAny(ct);
                if (!ReferenceEquals(client, remote) || ct.IsCancellationRequested) return;
                Queue(new("key", Code: 17, Down: true));
                Queue(new("key", Code: 86, Down: true));
                Queue(new("key", Code: 86));
                Queue(new("key", Code: 17));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { window.SessionStatus("붙여넣기 중단: " + UiText.Explain(ex, "다시 시도하세요.")); }
            finally { pasting = false; }
        }

        public void KeyUpRemote(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11) { e.Handled = true; return; }
            var code = NativeKey(e);
            if (code > 0) Queue(new("key", Code: code));
            e.Handled = true;
        }

        public void ToggleKorean(object sender, RoutedEventArgs e)
        {
            Queue(new("key", Code: 21, Down: true));
            Queue(new("key", Code: 21));
            window.ScreenImage.Focus();
        }

        public void InputControlChanged(object sender, RoutedEventArgs e)
        {
            var enabled = window.InputControlToggle.IsChecked == true;
            if (!enabled) Queue(new("release"));
            inputEnabled = enabled;
            UpdateInputChrome();
            if (client != null) window.SessionStatus(enabled ? "입력 제어 켜짐" : "입력 제어 꺼짐 · 화면 보기만 가능");
            PublishHealth();
        }

        public void ClipboardAutoChanged(object sender, RoutedEventArgs e)
        {
            var enabled = window.ClipboardAutoMenu.IsChecked == true;
            if (clipboardSync != null) clipboardSync.AutoEnabled = enabled;
            lastConnection.ClipboardAutoSync = enabled;
            SaveConnection();
            if (client != null) window.SessionStatus(enabled ? "클립보드 자동 동기화 켜짐" : "클립보드 자동 동기화 꺼짐");
        }

        public async void ClipboardSend(object sender, RoutedEventArgs e)
        {
            if (clipboardSync is not { } sync || connection is null) return;
            try { if (!await sync.PushAny(connection.Token)) window.SessionStatus("보낼 텍스트나 파일을 먼저 복사하세요."); }
            catch (Exception ex) { window.SessionStatus("클립보드 보내기 실패: " + UiText.Explain(ex, "다시 시도하세요.")); }
        }

        public async void ClipboardReceive(object sender, RoutedEventArgs e)
        {
            if (clipboardSync is not { } sync || connection is null) return;
            try { await sync.Pull(connection.Token); window.SessionStatus("상대 클립보드를 확인했습니다."); }
            catch (Exception ex) { window.SessionStatus("클립보드 받기 실패: " + UiText.Explain(ex, "다시 시도하세요.")); }
        }

        public void QualityMenuChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem selected || !int.TryParse(selected.Tag?.ToString(), out var quality)) return;
            frameQuality = Math.Clamp(quality, 25, 90);
            lastConnection.FrameQuality = frameQuality;
            SaveConnection();
            announcedDelivery = null;
            if (client != null && lastFrameQuality > 0)
            {
                AnnounceQuality(lastFrameQuality);
                if (QualityLabel(lastFrameQuality) == QualityLabel(frameQuality)) window.SessionStatus("화면 품질: " + selected.Header);
            }
            else
            {
                ShowSessionPreset();
                if (client != null) window.SessionStatus("화면 품질: " + selected.Header);
            }
        }

        public void FrameRateMenuChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem selected || !int.TryParse(selected.Tag?.ToString(), out var rate) || rate is not (5 or 10 or 15 or 30)) return;
            frameRate = rate;
            lastConnection.FrameRate = rate;
            SaveConnection();
            ShowSessionPreset();
            if (client != null) window.SessionStatus("최대 프레임 속도: " + rate + " FPS");
        }

        public void ShowSessionInfo(object sender, RoutedEventArgs e)
        {
            window.SessionInfoText.Text = DescribeSession();
            window.SessionInfoCard.Visibility = Visibility.Visible;
        }

        public void CloseSessionInfo(object sender, RoutedEventArgs e)
        {
            window.SessionInfoCard.Visibility = Visibility.Collapsed;
            if (client != null) window.ScreenImage.Focus();
        }

        public void RemoteFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateInputChrome();

        public void WindowActivationChanged() => UpdateInputChrome();

        private void UpdateInputChrome()
        {
            var controlling = RemoteInputFocused();
            window.Stage.BorderBrush = controlling ? (System.Windows.Media.Brush)window.FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
            window.Stage.BorderThickness = new Thickness(controlling ? 2 : 0);
            PlaceRemoteCursor();
            string? hint = null;
            if (client != null && !controlling)
                hint = inputEnabled
                    ? "화면을 클릭하면 키보드가 원격 PC로 갑니다."
                    : "입력 제어가 꺼져 있습니다. 화면만 보는 중입니다.";
            window.InputHintText.Text = hint ?? "";
            window.InputHint.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
            var alertLift = hint == null ? 16 : 58;
            window.SessionAlert.Margin = new Thickness(24, 16, 24, alertLift);
        }

        private string DescribeSession()
        {
            var display = window.DisplayList.SelectedItem as RemoteDisplay;
            var resolution = window.ScreenImage.Source is BitmapSource bitmap ? bitmap.PixelWidth + "×" + bitmap.PixelHeight : "수신 전";
            var measured = lastFrameBytes > 0;
            return "대상: " + lastConnection.Address
                + "\n모니터: " + (display?.Name ?? "선택 안 됨")
                + "\n수신 해상도: " + resolution
                + "\n요청 화질: " + QualityLabel(frameQuality) + " (" + frameQuality + ")"
                + "\n서버 화질: " + (measured && lastFrameQuality > 0 ? QualityLabel(lastFrameQuality) + " (" + lastFrameQuality + ")" : "측정 전")
                + "\n최대 프레임 속도: " + frameRate + " FPS"
                + "\n캡처: " + (measured ? lastCaptureMilliseconds.ToString("0.0") + " ms" : "측정 전")
                + "\n압축: " + (measured ? lastEncodeMilliseconds.ToString("0.0") + " ms" : "측정 전")
                + "\n프레임 크기: " + (measured ? (lastFrameBytes / 1024.0).ToString("0") + " KB" : "측정 전")
                + "\n화면 재사용: " + (measured ? (lastFrameReused ? "예" : "아니오") : "측정 전")
                + "\n왕복 지연: " + (roundTripMilliseconds < 0 ? "측정 중" : roundTripMilliseconds + " ms")
                + "\n입력 제어: " + (inputEnabled ? "켜짐" : "꺼짐")
                + "\n클립보드 자동 동기화: " + (window.ClipboardAutoMenu.IsChecked == true ? "켜짐" : "꺼짐");
        }

        private void SaveConnection()
        {
            try { lastConnection.Save(); }
            catch (Exception ex)
            {
                var message = "연결 정보 저장 실패: " + UiText.Explain(ex, "저장 위치를 확인하세요.");
                if (client == null) window.ClientNotice(message, problem: true);
                else window.SessionStatus(message);
            }
        }

        public void OpenSessionMenu(object sender, RoutedEventArgs e)
        {
            MarkChoice(0, frameQuality);
            MarkChoice(1, frameRate);
            window.SessionMenu.PlacementTarget = window.SessionMenuButton;
            window.SessionMenu.IsOpen = true;
        }

        private void MarkChoice(int index, int selected)
        {
            if (window.SessionMenu.Items[index] is not MenuItem parent) return;
            foreach (var item in parent.Items)
            {
                if (item is not MenuItem choice) continue;
                choice.IsCheckable = int.TryParse(choice.Tag?.ToString(), out var value);
                if (choice.IsCheckable) choice.IsChecked = value == selected;
            }
        }

        private async Task LoadDisplays()
        {
            if (client is not { } remote) return;
            var displays = await remote.Displays() ?? [];
            if (!ReferenceEquals(client, remote)) return;
            var wanted = selectedDisplay ?? lastConnection.Display;
            window.DisplayList.ItemsSource = displays;
            window.DisplayList.SelectedItem = displays.FirstOrDefault(d => d.Id == wanted) ?? displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
            window.DisplayList.Visibility = UiText.OfferMonitorChoice(displays.Length) ? Visibility.Visible : Visibility.Collapsed;
        }

        public async void RefreshDisplays(object sender, RoutedEventArgs e)
        {
            try { await LoadDisplays(); }
            catch (Exception ex) { window.SessionStatus(UiText.Explain(ex, "모니터 목록을 가져오지 못했습니다.")); }
        }

        public void DisplayChanged(object sender, SelectionChangedEventArgs e)
        {
            if (window.DisplayList.SelectedItem is not RemoteDisplay display) return;
            if (display.Id == selectedDisplay) return;
            Queue(new("release"));
            ClearFrame();
            ShowStage(client == null ? null : "화면을 받는 중…");
            renderedDisplay = null;
            selectedDisplay = display.Id;
            lastConnection.Display = display.Id;
            if (client != null) SaveConnection();
        }

        public void FullscreenKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11 && (fullscreen != null || window.ClientTab.IsSelected))
            {
                e.Handled = true;
                ToggleFullscreen(sender, e);
            }
        }

        public void ToggleFullscreen(object sender, RoutedEventArgs e)
        {
            if (fullscreen != null) { fullscreen.Close(); return; }
            Queue(new("release"));
            DetachSessionPanel();
            window.FullscreenAwayText.Text = UiText.FullscreenAway(lastConnection.Address);
            window.FullscreenPlaceholder.Visibility = Visibility.Visible;
            window.ConnectionControls.Visibility = Visibility.Collapsed;
            window.ClientPanel.Margin = new Thickness(0);
            window.Stage.CornerRadius = new CornerRadius(0);
            var shell = new Window { Title = "Personal Remote Desktop · F11 복귀", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = System.Windows.Media.Brushes.Black, Owner = window, Content = window.ClientPanel };
            shell.WindowStartupLocation = WindowStartupLocation.Manual;
            shell.Left = window.Left;
            shell.Top = window.Top;
            shell.Width = window.ActualWidth;
            shell.Height = window.ActualHeight;
            fullscreen = shell;
            window.RefreshIdentity();
            ApplySessionChrome();
            shell.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(shell).Handle)?.AddHook(NativeKeyboard);
            shell.PreviewKeyDown += FullscreenKey;
            shell.Activated += (_, _) => UpdateInputChrome();
            shell.Deactivated += (_, _) => { Queue(new("release")); UpdateInputChrome(); };
            shell.StateChanged += (_, _) => NoteViewerWatch();
            shell.Closed += (_, _) =>
            {
                Queue(new("release"));
                shell.Content = null;
                AttachSessionPanel();
                window.FullscreenPlaceholder.Visibility = Visibility.Collapsed;
                window.ConnectionControls.Visibility = client is null ? Visibility.Visible : Visibility.Collapsed;
                fullscreen = null;
                NoteViewerWatch();
                ApplySessionChrome();
                window.ScreenImage.Focus();
                UpdateInputChrome();
            };
            shell.Show();
            shell.WindowState = WindowState.Maximized;
            NoteViewerWatch();
            window.ScreenImage.Focus();
        }

        public void SetFullscreenTitle(string title)
        {
            if (fullscreen != null) fullscreen.Title = title;
        }

        public void ExitFullscreen() { if (fullscreen != null) fullscreen.Close(); }

        private void DetachSessionPanel()
        {
            if (window.ClientPanel.Parent is Panel panel) panel.Children.Remove(window.ClientPanel);
        }

        private void AttachSessionPanel()
        {
            if (window.ClientPanel.Parent == null) window.ClientHost.Children.Insert(0, window.ClientPanel);
        }

        public void ReleaseInput(object sender, KeyboardFocusChangedEventArgs e)
        {
            lock (suppressedSystemKeys) { remoteAltDown = false; suppressedSystemKeys.Clear(); }
            Queue(new("release"));
            UpdateInputChrome();
        }
    }
}
