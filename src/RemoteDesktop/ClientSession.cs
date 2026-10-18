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
        private const int InputQueueCapacity = 64;
        private const int InputQueueWarningThreshold = 48;
        private RemoteClient? client;
        private CancellationTokenSource? connection;
        private Task? connectionTask;
        private Task? connectTask;
        private ClipboardSync? clipboardSync;
        private bool pasting;
        private bool suppressPasteKeyUp;
        private Channel<InputEvent>? inputs;
        private Channel<byte>? inputSignal;
        private readonly object inputGate = new();
        private readonly InputBacklog inputBacklog = new();
        private long inputQueueWriteFailures;
        private int inputQueueThresholdWarned;
        private InputEvent? pendingMove;
        private InputEvent? pendingWheel;
        private readonly HashSet<int> pendingKeyDowns = [];
        private bool connecting;
        private bool reconnectWait;
        private bool connectedOnce;
        public string? RemoteEndpoint => client != null || holdingPicture ? lastConnection.Address : null;
        public bool IsFullscreenActive => fullscreen?.IsActive == true;
        public long InputQueueWriteFailures => Interlocked.Read(ref inputQueueWriteFailures);
        public bool IsFullscreen => fullscreen != null;
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
        private int pacedQuality = 55;
        private int paceSlow;
        private int paceFast;
        private readonly object viewGate = new();
        private PictureView pictureView = PictureView.Full;
        private PictureView sentView = PictureView.Full;
        private bool panCandidate;
        private bool panning;
        private double panStartX;
        private double panStartY;
        private PictureView panOrigin = PictureView.Full;
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
        private string? pictureCodec;
        private bool preferH264 = true;
        private int h264DecoderMisses;
        private int h264JpegFrames;
        private bool needKeyframe = true;
        private H264Decoder? videoDecoder;
        private readonly object videoGate = new();
        private WriteableBitmap? frameSurface;
        private nint systemShortcutHook;
        private KeyboardHookProc? systemShortcutHookProc;
        private readonly HashSet<int> suppressedSystemKeys = [];
        private readonly HashSet<int> localReleaseKeys = [];
        private bool remoteAltDown;
        private double toolbarOffsetX;
        private double toolbarOffsetY;
        private System.Windows.Threading.DispatcherTimer? sessionNoticeTimer;
        private string? settlingNotice;
        private string? secureNotice;
        private volatile bool viewerWatching = true;
        private string? sessionPassword;
        private Exception? connectFailure;
        private int reconnectAttempt;
        private int resumeEpoch;
        private bool suppressReconnect;
        private CancellationTokenSource? connectCancel;
        private bool connectCancelled;
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
            ResetPace();
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
            if (window.ServerTab.IsSelected) DismissInboundArrival();
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
            ResetPace();
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
            var work = SystemParameters.WorkArea;
            var sizing = WindowLayout.FitToWorkArea(work.Width, work.Height, window.Width, window.Height, window.MinWidth, window.MinHeight);
            window.MinWidth = sizing.MinWidth;
            window.MinHeight = sizing.MinHeight;
            window.Width = sizing.Width;
            window.Height = sizing.Height;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = work.Left + (work.Width - window.Width) / 2;
            window.Top = work.Top + (work.Height - window.Height) / 2;
            var screenLeft = SystemParameters.VirtualScreenLeft;
            var screenTop = SystemParameters.VirtualScreenTop;
            var screenWidth = SystemParameters.VirtualScreenWidth;
            var screenHeight = SystemParameters.VirtualScreenHeight;
            if (!lastConnection.TryGetWindowPlacement(screenLeft, screenTop, screenWidth, screenHeight, out var placement)) return;
            window.Width = Math.Clamp(placement.Width, sizing.MinWidth, sizing.MaxWidth);
            window.Height = Math.Clamp(placement.Height, sizing.MinHeight, sizing.MaxHeight);
            window.Left = Math.Clamp(placement.Left, screenLeft, screenLeft + screenWidth - window.Width);
            window.Top = Math.Clamp(placement.Top, screenTop, screenTop + screenHeight - window.Height);
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
        public Window DialogOwner(Window fallback) => fullscreen != null && fullscreen.IsVisible ? fullscreen : fallback;
        public Task DisconnectClient()
        {
            suppressReconnect = true;
            connectCancelled = true;
            sessionPassword = null;
            resumeEpoch++;
            ReleaseReconnectWait();
            return DisconnectClientCore();
        }

        private async Task DisconnectClientCore()
        {
            var pendingConnect = connectTask;
            var pendingConnection = connectionTask;
            var pendingConnectCancel = connectCancel;
            try { pendingConnectCancel?.Cancel(); }
            catch (Exception) { }
            try { connection?.Cancel(); }
            catch (Exception) { }

            var failures = new List<Exception>();
            try { await DisconnectCore(); }
            catch (Exception error) { failures.Add(error); }
            if (pendingConnect != null)
            {
                try { await pendingConnect; }
                catch (Exception error) { failures.Add(error); }
            }
            pendingConnection = connectionTask ?? pendingConnection;
            if (pendingConnection != null)
            {
                try { await pendingConnection; }
                catch (Exception error) { failures.Add(error); }
                if (ReferenceEquals(connectionTask, pendingConnection)) connectionTask = null;
            }
            if (ReferenceEquals(connectCancel, pendingConnectCancel))
            {
                try { connectCancel?.Dispose(); }
                catch (Exception error) { failures.Add(error); }
                connectCancel = null;
            }
            if (failures.Count > 0) throw new AggregateException("Client session cleanup failed.", failures);
        }

        private async Task ForgetReconnectsAfterStable(CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { return; }
            reconnectAttempt = 0;
        }

        private CancellationTokenSource ArmConnectCancel()
        {
            connectCancel?.Dispose();
            return connectCancel = new CancellationTokenSource();
        }

        private void ShowCancelButton()
        {
            window.ConnectButton.IsEnabled = true;
            window.ConnectButton.IsDefault = true;
            window.ConnectButton.Content = UiText.ConnectCancelLabel();
            var onPicture = window.ConnectionControls.Visibility != Visibility.Visible;
            window.ReconnectCancel.Visibility = onPicture ? Visibility.Visible : Visibility.Collapsed;
            window.StageHintCard.IsHitTestVisible = onPicture;
            if (onPicture && window.StageHintCard.Visibility != Visibility.Visible) ShowStage(UiText.ReconnectHold());
        }

        private void HideReconnectCancel()
        {
            window.ReconnectCancel.Visibility = Visibility.Collapsed;
            window.StageHintCard.IsHitTestVisible = false;
        }

        private void CancelOngoingConnect()
        {
            connectCancelled = true;
            suppressReconnect = true;
            sessionPassword = null;
            resumeEpoch++;
            try { connectCancel?.Cancel(); } catch (ObjectDisposedException) { }
        }

        private void ReleaseReconnectWait()
        {
            if (!reconnectWait) return;
            reconnectWait = false;
            try { connectCancel?.Cancel(); } catch (ObjectDisposedException) { }
        }

        private bool StopResume(int epoch)
        {
            if (!suppressReconnect && resumeEpoch == epoch && sessionPassword != null && client == null && !connecting) return false;
            if (connectCancelled && client == null && !connecting)
            {
                connectCancelled = false;
                RevealConnectForm(UiText.ConnectCancelled(), problem: false);
            }
            else if (client == null && !connecting && window.ConnectButton.Content as string == UiText.ConnectCancelLabel())
            {
                window.ConnectButton.IsEnabled = true;
                window.ConnectButton.IsDefault = false;
                window.ConnectButton.Content = connectedOnce ? "다시 접속" : "접속";
            }
            return true;
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
                var seconds = Math.Max(1, (int)wait.TotalSeconds);
                var cancel = ArmConnectCancel();
                reconnectWait = true;
                ShowCancelButton();
                SetConnectFormEnabled(false);
                SetHealth(UiText.ReconnectHold());
                try
                {
                    for (var left = seconds; left >= 1; left--)
                    {
                        if (StopResume(epoch)) return;
                        var notice = UiText.Reconnecting(reconnectAttempt, UiText.MaxAutomaticReconnects, left);
                        window.ClientNotice(notice);
                        ShowStage(notice);
                        window.SessionStatus(notice);
                        try { await Task.Delay(TimeSpan.FromSeconds(1), cancel.Token); }
                        catch (OperationCanceledException) { }
                    }
                    if (StopResume(epoch)) return;
                }
                finally { reconnectWait = false; }
                var password = sessionPassword;
                if (password == null) { StopResume(epoch); return; }
                if (await ConnectCore(password, announceFailure: false)) return;
                failure = connectFailure ?? failure;
            }
            StopResume(epoch);
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
            if (connecting || reconnectWait)
            {
                CancelOngoingConnect();
                return;
            }
            if (client != null) return;
            var form = UiText.CheckConnectForm(window.Address.Text, window.ClientPort.Text, CurrentClientPassword(), window.ServerPin.Text);
            if (form.Problem != null)
            {
                window.ClientNotice(form.Problem, problem: true);
                MarkConnectField(form.Field);
                return;
            }
            MarkConnectField("");
            suppressReconnect = false;
            connectCancelled = false;
            reconnectAttempt = 0;
            resumeEpoch++;
            var task = ConnectCore(CurrentClientPassword(), announceFailure: true);
            connectTask = task;
            try { await task; }
            finally { if (ReferenceEquals(connectTask, task)) connectTask = null; }
        }

        private void FocusConnectProblem(string field)
        {
            if (field == "address") window.Address.Focus();
            else if (field == "port") window.ClientPort.Focus();
            else if (field == "fingerprint")
            {
                window.ServerPin.Visibility = Visibility.Visible;
                window.ServerPin.Focus();
                window.ServerPin.CaretIndex = window.ServerPin.Text.Length;
            }
            else FocusClientPassword();
        }

        private void MarkConnectField(string field)
        {
            MarkField(window.Address, field == "address");
            MarkField(window.ClientPort, field == "port");
            MarkField(window.ClientPassword, field == "password");
            MarkField(window.ClientPasswordVisible, field == "password");
            MarkField(window.ServerPin, field == "fingerprint");
            if (field.Length > 0) FocusConnectProblem(field);
        }

        private static void MarkField(FrameworkElement element, bool invalid) => element.Tag = invalid ? UiText.InvalidField : null;

        private async Task<bool> ConnectCore(string password, bool announceFailure)
        {
            if (connecting || client != null) return false;
            connecting = true;
            var failureField = "";
            var cancel = ArmConnectCancel();
            ShowCancelButton();
            if (window.ConnectionControls.Visibility != Visibility.Visible) ShowStage(UiText.ReconnectHold());
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
                    pin = await PakeBootstrap.AcquireServerFingerprint(endpoint, password, cancel.Token);
                    window.ServerPin.Text = pin;
                }
                var remote = new RemoteClient(endpoint, pin);
                try { await remote.Login(password, cancel.Token); } catch { remote.Dispose(); throw; }
                if (attempt != generation) { try { await remote.Logout(); } finally { remote.Dispose(); } return false; }
                client = remote;
                preferH264 = true;
                h264DecoderMisses = 0;
                h264JpegFrames = 0;
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
                try { await LoadDisplays(cancel.Token); } catch { await DisconnectCore(retrying: !announceFailure); throw; }
                connection = new();
                inputs = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(InputQueueCapacity) { SingleReader = true, SingleWriter = false });
                inputSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
                inputBacklog.Reset();
                lock (inputGate) { pendingMove = null; pendingWheel = null; pendingKeyDowns.Clear(); }
                clipboardSync = new ClipboardSync(remote, new WindowsFileClipboard(), window.SessionStatus);
                clipboardSync.TransferringChanged += ShowTransferCancel;
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
                connectionTask = RunConnection(remote, connection.Token);
                return true;
            }
            catch (Exception ex)
            {
                if (connectCancelled)
                {
                    if (announceFailure && client == null)
                    {
                        connectCancelled = false;
                        window.ClientNotice(UiText.ConnectCancelled(), problem: false);
                    }
                    return false;
                }
                connectFailure = ex;
                if (announceFailure)
                {
                    window.ClientNotice("접속 실패: " + UiText.ConnectionFailure(ex, false), problem: true);
                    failureField = UiText.ConnectFailureField(ex);
                }
                return false;
            }
            finally
            {
                connecting = false;
                if (client != null || announceFailure)
                {
                    HideReconnectCancel();
                    window.ConnectButton.IsEnabled = client == null;
                    window.ConnectButton.IsDefault = false;
                    if (client == null) SetConnectFormEnabled(true);
                    if (client == null) window.ConnectButton.Content = connectedOnce ? "다시 접속" : "접속";
                    if (client == null && failureField.Length > 0) MarkConnectField(failureField);
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
                suppressClientPasswordReveal = true;
                window.ClientPassword.Password = window.ClientPasswordVisible.Text;
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
            window.ClientPassword.Tag = null;
            window.ClientPasswordVisible.Tag = null;
            suppressClientPasswordReveal = false;
        }

        public void Edited(FrameworkElement element)
        {
            if (Equals(element.Tag, UiText.InvalidField)) element.Tag = null;
        }

        public void PasswordEdited()
        {
            if (suppressClientPasswordReveal) return;
            Edited(window.ClientPassword);
            Edited(window.ClientPasswordVisible);
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
                    var asked = AskedQuality();
                    double transferMs = -1;
                    byte[] data;
                    RemoteFrame frame;
                    try
                    {
                        var viewerHasFrame = renderedDisplay == display && window.ScreenImage.Source != null;
                        var fetchStarted = Stopwatch.GetTimestamp();
                        var view = CurrentView();
                        var key = !viewerHasFrame || needKeyframe || !PictureZoom.Same(view, sentView);
                        frame = await remote.FrameDetailed(ct, display, asked, frameRate, inputEnabled, viewerHasFrame, cursorHash, key, view.X, view.Y, view.Width, view.Height, preferH264);
                        sentView = view;
                        data = frame.Bytes;
                        transferMs = data.Length == 0 ? -1 : Math.Max(0, Stopwatch.GetElapsedTime(fetchStarted).TotalMilliseconds - frame.CaptureMilliseconds - frame.EncodeMilliseconds - frame.WaitMilliseconds);
                        lastCaptureMilliseconds = frame.CaptureMilliseconds;
                        lastEncodeMilliseconds = frame.EncodeMilliseconds;
                        lastFrameReused = frame.Reused;
                        var deliveredQuality = frame.Quality > 0 ? frame.Quality : asked;
                        if (deliveredQuality != lastFrameQuality)
                        {
                            lastFrameQuality = deliveredQuality;
                            var snapshot = deliveredQuality;
                            var askedSnapshot = asked;
                            _ = window.Dispatcher.InvokeAsync(() => AnnounceQuality(snapshot, askedSnapshot));
                        }
                        if (data.Length > 0) lastFrameBytes = data.Length;
                        var cursorFrame = frame;
                        var secureText = UiText.SecureDesktopNotice(frame.Secure);
                        _ = window.Dispatcher.InvokeAsync(() => ApplyRemoteCursor(cursorFrame));
                        _ = window.Dispatcher.InvokeAsync(() => ShowSecureDesktop(secureText));
                    }
                    catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
                    { await LoadDisplays(); continue; }
                    var wantKey = NotePace(asked, transferMs, data.Length > 0);
                    if (display != selectedDisplay) continue;
                    if (data.Length > 0) await PresentFrameAsync(data, generation, frame.Codec);
                    if (wantKey) needKeyframe = true;
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
                        await Deliver(input);
                        if (input.Kind == "key" && input.Down) lock (inputGate) pendingKeyDowns.Remove(input.Code);
                    }
                    InputEvent? move, wheel;
                    lock (inputGate) { move = pendingMove; wheel = pendingWheel; pendingMove = null; pendingWheel = null; }
                    if (move != null) await Deliver(move);
                    if (wheel != null) await Deliver(wheel);
                    if (inputBacklog.Take())
                    {
                        lock (inputGate) pendingKeyDowns.Clear();
                        await Deliver(new("release"));
                    }
                    if (channel.Reader.CanCount && channel.Reader.Count < InputQueueWarningThreshold)
                        Interlocked.Exchange(ref inputQueueThresholdWarned, 0);
                }
                async Task Deliver(InputEvent input)
                {
                    var notice = await remote.Input(input, ct);
                    if (!string.IsNullOrEmpty(notice)) window.SessionStatus(notice);
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
            needKeyframe = true;
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
            if (!retrying) ResetVideo();
            if (!retrying) ResetPace();
            if (!retrying) SetView(PictureView.Full);
            panCandidate = false;
            panning = false;
            ShowStage(retrying ? UiText.ReconnectHold() : null);
            if (retrying) window.SessionStatus(UiText.ReconnectHold());
            window.SessionInfoCard.Visibility = Visibility.Collapsed;
            if (!retrying) selectedDisplay = null;
            SetSessionControlsEnabled(false);
            if (retrying)
            {
                window.ConnectionControls.Visibility = Visibility.Collapsed;
                window.SessionToolbar.Visibility = Visibility.Collapsed;
                window.SessionReveal.Visibility = Visibility.Visible;
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
            window.ConnectButton.IsDefault = false;
            window.ConnectButton.Content = retrying ? "다시 연결 중…" : connectedOnce ? "다시 접속" : "접속";
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
            window.ConnectButton.IsDefault = false;
            window.ConnectButton.Content = connectedOnce ? "다시 접속" : "접속";
            SetHealth("연결 종료");
            window.RefreshIdentity();
            window.ClientNotice(notice, problem);
            if (window.IsVisible && window.ClientTab.IsSelected) FocusClientPassword();
            HideReconnectCancel();
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
            ReleaseReconnectWait();
            await DisconnectCore();
            window.ClientNotice("연결 종료됨");
        }

        public void CancelReconnect(object sender, RoutedEventArgs e)
        {
            if (connecting || reconnectWait) CancelOngoingConnect();
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
            if (!enabled) window.DropHint.Visibility = Visibility.Collapsed;
            if (!enabled) window.TransferCancel.Visibility = Visibility.Collapsed;
            if (!enabled) sessionNoticeTimer?.Stop();
            if (!enabled) settlingNotice = null;
            if (!enabled) secureNotice = null;
            if (!enabled) { announcedDelivery = null; lastFrameQuality = 0; }
            UpdateInputChrome();
        }

        public void NoteSessionStatus()
        {
            if (client == null)
            {
                sessionNoticeTimer?.Stop();
                settlingNotice = null;
                window.SessionAlert.Visibility = Visibility.Collapsed;
                return;
            }
            ScheduleSessionBaseline();
            SyncSessionNotice(window.SessionToolbar.Visibility == Visibility.Visible);
        }

        public void AnnounceInbound(string? peer)
        {
            if (client == null) return;
            window.SessionStatus(UiText.InboundArrived(peer));
        }

        public void AnnounceHostNotice(string? text)
        {
            if (client == null || !UiText.HostSessionEnded(text) || UiText.ReconnectNotice(window.StatusText.Text)) return;
            window.SessionStatus(text!);
        }

        private void DismissInboundArrival()
        {
            if (client == null || !(UiText.InboundArrival(window.StatusText.Text) || UiText.HostSessionEnded(window.StatusText.Text))) return;
            sessionNoticeTimer?.Stop();
            settlingNotice = null;
            window.SessionStatus(UiText.SessionBaseline(inputEnabled));
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
            sessionNoticeTimer ??= new System.Windows.Threading.DispatcherTimer();
            sessionNoticeTimer.Interval = UiText.SessionNoticeDuration(text);
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

        private void ShowSecureDesktop(string? text)
        {
            if (secureNotice == text) return;
            secureNotice = text;
            SyncSessionNotice(window.SessionToolbar.Visibility == Visibility.Visible);
        }

        private void SyncSessionNotice(bool toolbarOpen)
        {
            var text = window.StatusText.Text;
            if (!string.IsNullOrEmpty(secureNotice))
            {
                window.SessionAlertText.Text = secureNotice;
                var showSecure = client != null && UiText.ShowSecureDesktop(toolbarOpen, secureNotice);
                window.SessionAlert.Visibility = showSecure ? Visibility.Visible : Visibility.Collapsed;
                if (!showSecure) return;
                window.SessionAlert.BorderBrush = (System.Windows.Media.Brush)window.FindResource("LineBrush");
                window.SessionAlertText.Foreground = (System.Windows.Media.Brush)window.FindResource("InkBrush");
                return;
            }
            var show = client != null && !toolbarOpen && UiText.SessionOnPicture(text);
            window.SessionAlert.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            var problem = UiText.IsProblem(text);
            window.SessionAlert.BorderBrush = (System.Windows.Media.Brush)window.FindResource(problem ? "DangerBrush" : "LineBrush");
            window.SessionAlertText.Foreground = (System.Windows.Media.Brush)window.FindResource(problem ? "DangerBrush" : "InkBrush");
        }

        private void SetHealth(string text)
        {
            var previous = healthDetail;
            if (previous == text) return;
            healthDetail = text;
            if (client == null) return;
            if (text == UiText.StaleFrame()) window.SessionStatus(text);
            else if (previous == UiText.StaleFrame() && window.StatusText.Text == UiText.StaleFrame())
                window.SessionStatus(UiText.SessionBaseline(inputEnabled));
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
                return;
            }
            if (client != null && SeenFrame() && UiText.FrameStale(DateTime.UtcNow - LastFrameUtc()) && healthDetail != UiText.StaleFrame())
                SetHealth(UiText.StaleFrame());
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

        private void OpenSessionToolbar()
        {
            if (client == null && !holdingPicture) return;
            window.SessionToolbar.Visibility = Visibility.Visible;
            window.SessionReveal.Visibility = Visibility.Collapsed;
            window.SessionToolbar.UpdateLayout();
            ClampSessionToolbarPosition(window.SessionToolbar);
            SyncSessionNotice(true);
        }

        private void CloseSessionToolbar()
        {
            if (client == null && !holdingPicture) return;
            window.SessionToolbar.Visibility = Visibility.Collapsed;
            window.SessionReveal.Visibility = Visibility.Visible;
            window.SessionReveal.UpdateLayout();
            ClampSessionToolbarPosition(window.SessionReveal);
            SyncSessionNotice(false);
        }

        private void ClampSessionToolbarPosition(FrameworkElement control)
        {
            var stageWidth = window.ClientPanel.ActualWidth;
            var stageHeight = window.ClientPanel.ActualHeight;
            var maxX = Math.Max(0, (stageWidth - control.ActualWidth - control.Margin.Left - control.Margin.Right) / 2);
            var minY = -control.Margin.Top;
            var maxY = Math.Max(minY, stageHeight - control.ActualHeight - control.Margin.Top - 8);
            toolbarOffsetX = Math.Clamp(toolbarOffsetX, -maxX, maxX);
            toolbarOffsetY = Math.Clamp(toolbarOffsetY, minY, maxY);
            window.SessionToolbar.RenderTransform = new TranslateTransform(toolbarOffsetX, toolbarOffsetY);
            window.SessionReveal.RenderTransform = new TranslateTransform(toolbarOffsetX, toolbarOffsetY);
        }

        public void RevealSessionToolbar(object sender, RoutedEventArgs e) => OpenSessionToolbar();

        public void CollapseSessionToolbar(object sender, RoutedEventArgs e) => CloseSessionToolbar();

        public void SessionToolbarDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (window.SessionToolbar.Visibility == Visibility.Visible)
            {
                window.SessionToolbar.UpdateLayout();
                toolbarOffsetX += e.HorizontalChange;
                toolbarOffsetY += e.VerticalChange;
                ClampSessionToolbarPosition(window.SessionToolbar);
            }
            else if (window.SessionReveal.Visibility == Visibility.Visible)
            {
                window.SessionReveal.UpdateLayout();
                toolbarOffsetX += e.HorizontalChange;
                toolbarOffsetY += e.VerticalChange;
                ClampSessionToolbarPosition(window.SessionReveal);
            }
        }

        public void SessionViewportSizeChanged()
        {
            var control = window.SessionToolbar.Visibility == Visibility.Visible
                ? window.SessionToolbar
                : window.SessionReveal.Visibility == Visibility.Visible ? window.SessionReveal : null;
            if (control == null) return;
            ClampSessionToolbarPosition(control);
        }

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
            signal.Writer.TryWrite(0);
        }

        private bool TryCoalesceWheel(Channel<InputEvent> channel, int delta)
        {
            WarnIfInputQueueNearCapacity(channel);
            var sum = (long)(pendingWheel?.Code ?? 0) + delta;
            if (sum is >= -InputEvent.MaxWheelDelta and <= InputEvent.MaxWheelDelta)
            {
                pendingWheel = sum == 0 ? null : new InputEvent("wheel", Code: (int)sum);
                return true;
            }
            if (pendingMove != null && !TryWriteInput(channel, pendingMove)) return false;
            pendingMove = null;
            pendingWheel = null;
            while (sum is < -InputEvent.MaxWheelDelta or > InputEvent.MaxWheelDelta)
            {
                var chunk = sum > 0 ? InputEvent.MaxWheelDelta : -InputEvent.MaxWheelDelta;
                if (!TryWriteInput(channel, new InputEvent("wheel", Code: chunk))) return false;
                sum -= chunk;
            }
            pendingWheel = sum == 0 ? null : new InputEvent("wheel", Code: (int)sum);
            return true;
        }

        private bool EnqueueDiscrete(Channel<InputEvent> channel, InputEvent input)
        {
            WarnIfInputQueueNearCapacity(channel);
            if (pendingMove != null && !TryWriteInput(channel, pendingMove)) return false;
            pendingMove = null;
            if (pendingWheel != null && !TryWriteInput(channel, pendingWheel)) return false;
            pendingWheel = null;
            if (input.Kind == "key" && !input.Down) pendingKeyDowns.Remove(input.Code);
            return TryWriteInput(channel, input);
        }

        private void WarnIfInputQueueNearCapacity(Channel<InputEvent> channel)
        {
            if (!channel.Reader.CanCount || channel.Reader.Count < InputQueueWarningThreshold) return;
            if (Interlocked.Exchange(ref inputQueueThresholdWarned, 1) == 0)
                System.Diagnostics.Trace.TraceWarning("Remote input queue reached {0}/{1} entries.", channel.Reader.Count, InputQueueCapacity);
        }

        private bool TryWriteInput(Channel<InputEvent> channel, InputEvent input)
        {
            if (channel.Writer.TryWrite(input)) return true;
            var failures = Interlocked.Increment(ref inputQueueWriteFailures);
            if (failures == 1 || (failures % InputQueueCapacity) == 0)
                System.Diagnostics.Trace.TraceWarning("Remote input queue write failed; total failures={0}.", failures);
            return false;
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
            window.StageHintCard.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
            if (hint == null) HideReconnectCancel();
        }

        private void ShowSessionPreset()
        {
            var delivered = client != null && lastFrameQuality > 0 ? QualityLabel(lastFrameQuality) : null;
            window.SessionPresetText.Text = UiText.SessionPreset(client == null ? null : lastConnection.Address, QualityLabel(frameQuality), frameRate, delivered, client == null ? null : pictureCodec);
        }

        private void AnnounceQuality(int quality, int asked)
        {
            if (client == null || quality <= 0) return;
            lastFrameQuality = quality;
            var requested = QualityLabel(asked);
            var delivered = QualityLabel(quality);
            var notice = UiText.QualityShift(requested, announcedDelivery, delivered);
            announcedDelivery = delivered;
            ShowSessionPreset();
            if (notice != null) window.SessionStatus(notice);
        }

        private static string QualityLabel(int quality) => UiText.QualityName(quality);

        private int QualityCeiling() => frameQuality is >= 25 and <= 90 ? frameQuality : 55;

        private int AskedQuality()
        {
            var ceiling = QualityCeiling();
            var current = pacedQuality is >= 25 and <= 90 ? pacedQuality : ceiling;
            return Math.Min(current, ceiling);
        }

        private void ResetPace()
        {
            pacedQuality = QualityCeiling();
            paceSlow = 0;
            paceFast = 0;
        }

        private bool NotePace(int asked, double transferMs, bool picture)
        {
            var step = VideoCodec.Pace(QualityCeiling(), asked, transferMs, frameRate, paceSlow, paceFast, picture);
            paceSlow = step.Slow;
            paceFast = step.Fast;
            if (!step.Changed) return false;
            pacedQuality = step.Quality;
            var ceiling = QualityCeiling();
            var from = asked;
            var to = step.Quality;
            _ = window.Dispatcher.InvokeAsync(() =>
            {
                announcedDelivery = QualityLabel(to);
                ShowSessionPreset();
                var notice = UiText.PaceShift(ceiling, from, to);
                if (!string.IsNullOrEmpty(notice)) window.SessionStatus(notice);
            });
            return true;
        }

        private readonly record struct DecodedFrame(int Width, int Height, double DpiX, double DpiY, int Stride, byte[] Pixels);
        private enum PictureRead { None, Frame, Failed }
        private readonly record struct PictureDecode(PictureRead Read, DecodedFrame Frame);

        private static DecodedFrame DecodeFrame(byte[] jpeg)
        {
            if (!FrameLimits.TryReadJpegDimensions(jpeg, out var headerWidth, out var headerHeight) || !FrameLimits.IsSupported(headerWidth, headerHeight))
                throw new InvalidOperationException("JPEG 프레임 크기가 지원 범위를 벗어났습니다.");
            using var stream = new MemoryStream(jpeg, writable: false);
            using var bitmap = new System.Drawing.Bitmap(stream);
            var width = bitmap.Width;
            var height = bitmap.Height;
            if (width != headerWidth || height != headerHeight || !FrameLimits.IsSupported(width, height))
                throw new InvalidOperationException("원격 화면 크기가 올바르지 않습니다.");
            System.Drawing.Imaging.BitmapData? bits = null;
            byte[]? pixels = null;
            try
            {
                bits = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var stride = bits.Stride;
                if (stride <= 0) throw new InvalidOperationException("화면 프레임 방향이 올바르지 않습니다.");
                var count = checked(stride * height);
                if (count > FrameLimits.MaxDecodedBytes) throw new InvalidOperationException("원격 화면 프레임이 너무 큽니다.");
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

        private static DecodedFrame CopyFrame(byte[] pixels, int width, int height)
        {
            var count = FrameLimits.CheckedByteCount(width, height, 4);
            if (pixels.Length < count) throw new InvalidOperationException("H.264 프레임 크기가 올바르지 않습니다.");
            var stride = checked(width * 4);
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(count);
            Buffer.BlockCopy(pixels, 0, rented, 0, count);
            return new DecodedFrame(width, height, 96, 96, stride, rented);
        }

        private PictureDecode ReadPicture(byte[] data, string codec)
        {
            if (VideoCodec.Normalize(codec) != "h264") return new(PictureRead.Frame, DecodeFrame(data));
            lock (videoGate)
            {
                try
                {
                    videoDecoder ??= new H264Decoder();
                    if (!videoDecoder.TryDecode(data, out var pixels, out var width, out var height)) return new(PictureRead.None, default);
                    return new(PictureRead.Frame, CopyFrame(pixels, width, height));
                }
                catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException)
                {
                    return new(PictureRead.Failed, default);
                }
            }
        }

        private void ResetVideo()
        {
            lock (videoGate)
            {
                videoDecoder?.Dispose();
                videoDecoder = null;
            }
            pictureCodec = null;
        }

        private async Task PresentFrameAsync(byte[] data, int attempt, string codec)
        {
            if (attempt != generation) return;
            var decoded = await Task.Run(() => ReadPicture(data, codec));
            if (decoded.Read != PictureRead.Frame)
            {
                if (VideoCodec.Normalize(codec) == "h264" && preferH264)
                {
                    h264JpegFrames = 0;
                    h264DecoderMisses = H264FallbackPolicy.NoteDecoderMiss(h264DecoderMisses);
                    needKeyframe = true;
                    if (decoded.Read == PictureRead.Failed) ResetVideo();
                    if (H264FallbackPolicy.DecoderShouldUseJpeg(h264DecoderMisses)) SwitchToJpeg();
                }
                else if (decoded.Read == PictureRead.Failed)
                {
                    needKeyframe = true;
                    ResetVideo();
                }
                return;
            }
            try
            {
                if (attempt != generation || client == null) return;
                var frame = decoded.Frame;
                if (frameSurface == null || frameSurface.PixelWidth != frame.Width || frameSurface.PixelHeight != frame.Height)
                {
                    frameSurface = new WriteableBitmap(frame.Width, frame.Height, frame.DpiX, frame.DpiY, System.Windows.Media.PixelFormats.Bgra32, null);
                    window.ScreenImage.Source = frameSurface;
                }
                frameSurface.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Stride, 0);
                needKeyframe = false;
                var shown = VideoCodec.Normalize(codec);
                if (shown == "h264")
                {
                    h264DecoderMisses = 0;
                    h264JpegFrames = 0;
                }
                else if (preferH264)
                {
                    h264JpegFrames = H264FallbackPolicy.NoteServerJpegFrame(h264JpegFrames);
                    if (H264FallbackPolicy.ServerShouldUseJpeg(h264JpegFrames)) SwitchToJpeg();
                }
                if (pictureCodec != shown)
                {
                    pictureCodec = shown;
                    ShowSessionPreset();
                }
                ApplyPictureScaling();
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(decoded.Frame.Pixels); }
        }

        private void SwitchToJpeg()
        {
            if (!preferH264) return;
            preferH264 = false;
            h264DecoderMisses = 0;
            h264JpegFrames = 0;
            needKeyframe = true;
            ResetVideo();
            window.SessionStatus(UiText.VideoFallback());
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
            if (!TryPicturePoint(e, out var x, out var y)) return false;
            var point = PictureZoom.ToDisplay(CurrentView(), x, y);
            Queue(new("move", point.X, point.Y, Display: renderedDisplay));
            return true;
        }

        private bool TryPicturePoint(MouseEventArgs e, out double x, out double y)
        {
            x = 0;
            y = 0;
            if (window.ScreenImage.Source is not BitmapSource image) return false;
            var scale = Math.Min(window.ScreenImage.ActualWidth / image.PixelWidth, window.ScreenImage.ActualHeight / image.PixelHeight);
            var w = image.PixelWidth * scale;
            var h = image.PixelHeight * scale;
            if (w <= 0 || h <= 0) return false;
            var p = e.GetPosition(window.ScreenImage);
            x = (p.X - (window.ScreenImage.ActualWidth - w) / 2) / w;
            y = (p.Y - (window.ScreenImage.ActualHeight - h) / 2) / h;
            if (!window.ScreenImage.IsMouseCaptured && (x < 0 || x > 1 || y < 0 || y > 1)) return false;
            x = Math.Clamp(x, 0, 1);
            y = Math.Clamp(y, 0, 1);
            return true;
        }

        public void Move(object sender, MouseEventArgs e)
        {
            var stageY = e.GetPosition(window.Stage).Y;
            lastPointer = e.GetPosition(window.ScreenImage);
            PlaceRemoteCursor();
            if (panCandidate)
            {
                if (TryPicturePoint(e, out var x, out var y))
                {
                    var dx = x - panStartX;
                    var dy = y - panStartY;
                    if (!panning && (Math.Abs(dx) > 0.01 || Math.Abs(dy) > 0.01)) panning = true;
                    if (panning) ApplyView(PictureZoom.Pan(panOrigin, dx, dy), false);
                }
                e.Handled = true;
                return;
            }
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
                e.Handled = true;
                return;
            }
            if (!Position(e)) return;
            window.ScreenImage.CaptureMouse();
            if (e.ChangedButton == MouseButton.Middle && PictureZoom.CanPan(CurrentView()))
            {
                if (!TryPicturePoint(e, out panStartX, out panStartY))
                {
                    window.ScreenImage.ReleaseMouseCapture();
                    e.Handled = true;
                    return;
                }
                panCandidate = true;
                panning = false;
                panOrigin = CurrentView();
                e.Handled = true;
                return;
            }
            Queue(new("button", Code: Button(e.ChangedButton), Down: true));
            e.Handled = true;
        }

        public void MouseUpRemote(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && panCandidate)
            {
                var click = !panning;
                panCandidate = false;
                panning = false;
                window.ScreenImage.ReleaseMouseCapture();
                if (click && Position(e))
                {
                    Queue(new("button", Code: 2, Down: true));
                    Queue(new("button", Code: 2));
                }
                e.Handled = true;
                return;
            }
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
            if ((connecting || reconnectWait) && e.Key is Key.Enter or Key.Return)
            {
                e.Handled = true;
                CancelOngoingConnect();
                return;
            }
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
            if (code < 0) return CallNextHookEx(systemShortcutHook, code, message, data);
            if (!RemoteInputFocused() && !HasLocalRelease())
            {
                ReleaseSystemShortcuts();
                return CallNextHookEx(systemShortcutHook, code, message, data);
            }
            var keyboard = Marshal.PtrToStructure<KeyboardHookData>(data);
            var key = (int)keyboard.VirtualKey;
            var down = message is 0x0100 or 0x0104;
            var up = message is 0x0101 or 0x0105;
            if (up && EatLocalRelease(key)) return 1;
            if (!RemoteInputFocused())
            {
                ReleaseSystemShortcuts();
                return CallNextHookEx(systemShortcutHook, code, message, data);
            }
            if (!down && !up) return CallNextHookEx(systemShortcutHook, code, message, data);
            if (down && TakeLocalRelease(key)) return 1;
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

        private bool TakeLocalRelease(int key)
        {
            if (!RemoteInputFocused() || !UiText.CompletesReleaseChord(key, ModifierDown(0x11), ModifierDown(0x12), ModifierDown(0x10))) return false;
            lock (localReleaseKeys) localReleaseKeys.Add(key);
            window.Dispatcher.BeginInvoke(ReleaseKeyboardToLocal);
            return true;
        }

        private bool EatLocalRelease(int key)
        {
            lock (localReleaseKeys) return localReleaseKeys.Remove(key);
        }

        private bool HasLocalRelease()
        {
            lock (localReleaseKeys) return localReleaseKeys.Count > 0;
        }

        private void ReleaseKeyboardToLocal()
        {
            if (client == null || !window.ScreenImage.IsKeyboardFocused) return;
            lock (suppressedSystemKeys)
            lock (localReleaseKeys)
                foreach (var held in suppressedSystemKeys) localReleaseKeys.Add(held);
            window.SessionStatus(UiText.InputReleased());
            window.InputControlToggle.Focus();
            if (window.ScreenImage.IsKeyboardFocused) System.Windows.Input.Keyboard.ClearFocus();
        }

        private bool ModifierDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

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
                if (!down && EatLocalRelease(code)) { handled = true; return 0; }
                if (down && ((long)lParam & (1L << 30)) == 0 && TakeLocalRelease(code)) { handled = true; return 0; }
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

        public void SendSecureAttention(object sender, RoutedEventArgs e)
        {
            Queue(new(SecureAttention.Kind));
            window.ScreenImage.Focus();
        }

        public void InputControlChanged(object sender, RoutedEventArgs e)
        {
            var enabled = window.InputControlToggle.IsChecked == true;
            if (!enabled) Queue(new("release"));
            inputEnabled = enabled;
            UpdateInputChrome();
            if (client != null) window.SessionStatus(enabled ? "입력 제어 켜짐" : "입력 제어 꺼짐 · 화면 보기만 가능");
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
            try { if (await sync.Pull(connection.Token, manual: true)) window.SessionStatus("상대 클립보드를 확인했습니다."); }
            catch (Exception ex) { window.SessionStatus("클립보드 받기 실패: " + UiText.Explain(ex, "다시 시도하세요.")); }
        }

        public void FileDrag(object sender, DragEventArgs e)
        {
            var files = client != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            var allowed = CanDropFiles(e.Data);
            e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            window.DropHintText.Text = UiText.FileDropHint(inputEnabled);
            window.DropHint.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        }

        public void FileDragLeave(object sender, DragEventArgs e)
        {
            var point = e.GetPosition(window.Stage);
            if (point.X >= 0 && point.Y >= 0 && point.X < window.Stage.ActualWidth && point.Y < window.Stage.ActualHeight) return;
            window.DropHint.Visibility = Visibility.Collapsed;
        }

        public async void FileDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            window.DropHint.Visibility = Visibility.Collapsed;
            if (client == null || connection is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (!inputEnabled) { window.SessionStatus(UiText.FileDropHint(false)); return; }
            if (clipboardSync is not { } sync || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            window.SessionStatus(UiText.FileDropStarted(paths.Length));
            try { await sync.PushFiles(paths, connection.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { window.SessionStatus("파일 보내기 실패: " + UiText.Explain(ex, "다시 시도하세요.")); }
        }

        public void CancelTransfer(object sender, RoutedEventArgs e) => clipboardSync?.CancelTransfer();

        private void ShowTransferCancel(bool busy)
        {
            window.Dispatcher.InvokeAsync(() => window.TransferCancel.Visibility = busy && client != null ? Visibility.Visible : Visibility.Collapsed);
        }

        private bool CanDropFiles(System.Windows.IDataObject data)
            => client != null && inputEnabled && connection != null && data.GetDataPresent(DataFormats.FileDrop);

        public void QualityMenuChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem selected || !int.TryParse(selected.Tag?.ToString(), out var quality)) return;
            frameQuality = Math.Clamp(quality, 25, 90);
            lastConnection.FrameQuality = frameQuality;
            SaveConnection();
            ResetPace();
            needKeyframe = true;
            announcedDelivery = null;
            if (client != null && lastFrameQuality > 0)
            {
                AnnounceQuality(lastFrameQuality, frameQuality);
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
            paceSlow = 0;
            paceFast = 0;
            ShowSessionPreset();
            if (client != null) window.SessionStatus("최대 프레임 속도: " + rate + " FPS");
        }

        public void ZoomMenuChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem selected || !double.TryParse(selected.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var zoom)) return;
            ApplyView(PictureZoom.At(CurrentView(), zoom, 0.5, 0.5), true);
        }

        private PictureView CurrentView()
        {
            lock (viewGate) return pictureView;
        }

        private void SetView(PictureView next)
        {
            lock (viewGate) pictureView = next;
        }

        private void ApplyView(PictureView next, bool announce)
        {
            if (PictureZoom.Same(CurrentView(), next)) return;
            SetView(next);
            if (announce && client != null) window.SessionStatus(PictureZoom.Notice(next));
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
                    ? UiText.InputHint(true)
                    : UiText.InputHint(false);
            window.InputHintText.Text = hint ?? "";
            window.InputHint.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
            window.InputControlToggle.ToolTip = UiText.InputReleaseTip();
            var alertLift = hint == null ? 16 : inputEnabled ? 76 : 58;
            window.SessionAlert.Margin = new Thickness(24, 16, 24, alertLift);
            window.TransferCancel.Margin = new Thickness(0, 0, 0, alertLift + 48);
        }

        private string DescribeSession()
        {
            var row = window.DisplayList.SelectedItem as MonitorRow;
            var resolution = window.ScreenImage.Source is BitmapSource bitmap ? bitmap.PixelWidth + "×" + bitmap.PixelHeight : "수신 전";
            var measured = lastFrameBytes > 0;
            var elapsed = viewingSince == null ? "확인 중" : UiText.ElapsedClock(DateTime.UtcNow - viewingSince.Value);
            return "연결 시간: " + elapsed
                + "\n대상: " + lastConnection.Address
                + "\n모니터: " + (string.IsNullOrWhiteSpace(row?.Label) ? "선택 안 됨" : row!.Label)
                + "\n수신 해상도: " + resolution
                + "\n요청 화질: " + QualityLabel(frameQuality) + " (" + frameQuality + ")"
                + "\n전송 화질: " + (AskedQuality() == frameQuality ? "요청과 같음" : QualityLabel(pacedQuality) + " (" + pacedQuality + ")")
                + "\n서버 화질: " + (measured && lastFrameQuality > 0 ? QualityLabel(lastFrameQuality) + " (" + lastFrameQuality + ")" : "측정 전")
                + "\n최대 프레임 속도: " + frameRate + " FPS"
                + "\n확대: " + (PictureZoom.CanPan(CurrentView()) ? CurrentView().Zoom.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "배" : "원래 크기")
                + "\n캡처: " + (measured ? lastCaptureMilliseconds.ToString("0.0") + " ms" : "측정 전")
                + "\n압축: " + (measured ? lastEncodeMilliseconds.ToString("0.0") + " ms" : "측정 전")
                + "\n프레임 크기: " + (measured ? (lastFrameBytes / 1024.0).ToString("0") + " KB" : "측정 전")
                + "\n화면 재사용: " + (measured ? (lastFrameReused ? "예" : "아니오") : "측정 전")
                + "\n왕복 지연: " + (roundTripMilliseconds < 0 ? "측정 중" : roundTripMilliseconds + " ms")
                + "\n입력 제어: " + (inputEnabled ? "켜짐" : "꺼짐")
                + "\n" + UiText.InputReleaseTip()
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

        private async Task LoadDisplays(CancellationToken cancellationToken = default)
        {
            if (client is not { } remote) return;
            var displays = await remote.Displays(cancellationToken) ?? [];
            if (!ReferenceEquals(client, remote)) return;
            var wanted = selectedDisplay ?? lastConnection.Display;
            var monitors = displays.Where(d => d.Id != "all").ToArray();
            var spots = monitors.Select(d => new UiText.MonitorSpot(d.Left, d.Top, d.Width, d.Height)).ToArray();
            var rows = displays.Where(d => d.Id == "all").Concat(monitors.OrderBy(d => d.Top).ThenBy(d => d.Left)).Select(d => new MonitorRow(d, UiText.DisplayChoice(d.Name, UiText.DisplayPlace(d.Id == "all", d.Left, d.Top, d.Width, d.Height, spots), d.Width, d.Height, d.Primary, d.Id == "all"))).ToArray();
            window.DisplayList.ItemsSource = rows;
            var selected = rows.FirstOrDefault(d => d.Display.Id == wanted) ?? rows.FirstOrDefault(d => d.Display.Primary) ?? rows.FirstOrDefault();
            window.DisplayList.SelectedItem = selected;
            window.DisplayList.ToolTip = selected?.Label;
            window.DisplayList.Visibility = UiText.OfferMonitorChoice(displays.Length) ? Visibility.Visible : Visibility.Collapsed;
        }

        public async void RefreshDisplays(object sender, RoutedEventArgs e)
        {
            try { await LoadDisplays(); }
            catch (Exception ex) { window.SessionStatus(UiText.Explain(ex, "모니터 목록을 가져오지 못했습니다.")); }
        }

        public void DisplayChanged(object sender, SelectionChangedEventArgs e)
        {
            if (window.DisplayList.SelectedItem is not MonitorRow row) return;
            var display = row.Display;
            if (display.Id == selectedDisplay) return;
            Queue(new("release"));
            ClearFrame();
            if (client == null) ShowStage(null);
            else
            {
                var notice = UiText.SwitchingDisplay(row.Label);
                ShowStage(notice);
                window.SessionStatus(notice);
            }
            renderedDisplay = null;
            selectedDisplay = display.Id;
            SetView(PictureView.Full);
            lastConnection.Display = display.Id;
            window.DisplayList.ToolTip = row.Label;
            if (client != null) SaveConnection();
        }

        public void FullscreenKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && UiText.EscapeLeavesFullscreen(fullscreen != null, RemoteInputFocused()))
            {
                e.Handled = true;
                fullscreen?.Close();
                return;
            }
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
            var shell = new Window { Title = "Personal Remote Desktop · F11 또는 Esc로 복귀", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = System.Windows.Media.Brushes.Black, Owner = window, Content = window.ClientPanel };
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

        private sealed class MonitorRow
        {
            public MonitorRow(RemoteDisplay display, string label)
            {
                Display = display;
                Label = label;
            }

            public RemoteDisplay Display { get; }
            public string Label { get; }
        }
    }
}
