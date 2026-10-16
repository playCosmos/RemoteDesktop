using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace RemoteDesktop;

public partial class MainWindow : Window
{
    private readonly HostSettings settings = HostSettings.Load();
    private readonly X509Certificate2 certificate = HostSettings.Certificate();
    private RemoteHost? host;
    private RemoteClient? client;
    private CancellationTokenSource? connection;
    private ClipboardSync? clipboardSync;
    private bool pasting;
    private bool suppressPasteKeyUp;
    private Channel<InputEvent>? inputs;
    private bool connecting;
    private long lastMove;
    private int generation;
    private readonly ClientSettings lastConnection = ClientSettings.Load();
    private string? selectedDisplay;
    private string? renderedDisplay;
    private Window? fullscreen;
    private int unicodePackets;
    private bool hostStarting;
    private bool closing;
    private bool changingPassword;
    private bool inputEnabled = true;
    private int frameQuality;
    private int frameRate;
    private int framesSinceHealth;
    private DateTime healthWindowStarted = DateTime.UtcNow;
    private long roundTripMilliseconds = -1;
    private double lastCaptureMilliseconds;
    private double lastEncodeMilliseconds;
    private bool lastFrameReused;
    private int lastFrameQuality;
    private int lastFrameBytes;
    private nint systemShortcutHook;
    private KeyboardHookProc? systemShortcutHookProc;
    private readonly HashSet<int> suppressedSystemKeys = [];
    private bool remoteAltDown;
    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is not null) Title = "Personal Remote Desktop " + version.ToString(3);
        ClientTab.IsSelected = true;
        ServerTab.Visibility = Visibility.Visible;
        SmoothHostToggle.IsChecked = settings.UseSmoothHost;
        var endpoint = SplitEndpoint(lastConnection.Address);
        Address.Text = endpoint.Host; ClientPort.Text = endpoint.Port; ServerPin.Text = lastConnection.Fingerprint;
        frameQuality = Math.Clamp(lastConnection.FrameQuality, 25, 90);
        frameRate = lastConnection.FrameRate is 5 or 10 or 15 or 30 ? lastConnection.FrameRate : 15;
        ClipboardAutoMenu.IsChecked = lastConnection.ClipboardAutoSync;
        PreviewKeyDown += FullscreenKey;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(NativeKeyboard);
            InstallSystemShortcutHook();
        };
        HostPort.Text = settings.Port.ToString(); UpdatePasswordState();
        Fingerprint.Text = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        Addresses.Text = "이 PC 주소: " + string.Join(", ", Dns.GetHostAddresses(Dns.GetHostName()).Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
        Loaded += async (_, _) =>
        {
            if (settings.HasPassword) await StartHostAsync(automatic: true);
            else { SetHostState(HostUiState.Stopped); HostStatus("서버 비밀번호를 한 번 설정하면 다음 실행부터 자동으로 접속 대기를 시작합니다."); }
        };
        Closing += (_, _) => closing = true;
        Closed += async (_, _) => { UninstallSystemShortcutHook(); fullscreen?.Close(); await DisconnectClient(); if (host != null) await host.DisposeAsync(); certificate.Dispose(); };
        Deactivated += (_, _) => Queue(new("release"));
    }
    private void SmoothHostChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || SmoothHostToggle.IsChecked is not bool enabled) return;
        settings.SmoothHost = enabled;
        try { settings.Save(); }
        catch (Exception ex) { HostStatus(ex.Message); }
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
        KickButton.IsEnabled = host?.HasSession == true && StopButton.IsEnabled;
    });
    private void SessionStatus(string text) => Dispatcher.InvokeAsync(() => StatusText.Text = text);
    private void ClientNotice(string text, bool problem = false) => Dispatcher.InvokeAsync(() =>
    {
        ClientNoticeText.Text = text;
        ClientNoticeText.Foreground = problem
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");
    });
    
    private enum HostUiState { Stopped, Starting, Listening, Stopping }
    private void SetHostState(HostUiState state)
    {
        StartButton.Content = state switch
        {
            HostUiState.Starting => "접속 대기 시작 중…",
            HostUiState.Listening => "접속 대기 중",
            HostUiState.Stopping => "접속 대기 중지 중…",
            _ => "접속 대기 시작"
        };
        StartButton.IsEnabled = state == HostUiState.Stopped;
        StopButton.IsEnabled = state == HostUiState.Listening;
        KickButton.IsEnabled = state == HostUiState.Listening && host?.HasSession == true;
        HostPort.IsEnabled = ChangePasswordButton.IsEnabled = state == HostUiState.Stopped;
        SmoothHostToggle.IsEnabled = state == HostUiState.Stopped;
    }
    private void ShowListening(int port)
    {
        SetHostState(HostUiState.Listening);
        var addresses = string.Join(", ", Dns.GetHostAddresses(Dns.GetHostName()).Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
        HostStatus("접속 대기 중 · 포트 " + port + " · " + addresses);
    }
    private void UpdatePasswordState()
    {
        if (!settings.HasPassword)
        {
            PasswordState.Text = "접속 비밀번호를 한 번 설정하세요.";
            HostPasswordInstruction.Text = "접속 비밀번호 (8자 이상 · 한 번 저장하면 계속 사용)";
            SaveHostPasswordButton.Content = "비밀번호 저장";
            PasswordEditor.Visibility = Visibility.Visible;
            ChangePasswordButton.Visibility = Visibility.Collapsed;
        }
        else if (!settings.HasOpaqueIdentity)
        {
            PasswordState.Text = "저장된 서버 비밀번호는 유지되어 있습니다. 빈 입력란은 보안상 정상이며, 첫 지문 등록을 위해 현재 비밀번호를 한 번 다시 입력하세요.";
            HostPasswordInstruction.Text = "현재 저장된 서버 비밀번호 (변경되지 않음)";
            SaveHostPasswordButton.Content = "보안 등록";
            PasswordEditor.Visibility = Visibility.Visible;
            ChangePasswordButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            PasswordState.Text = "비밀번호 저장됨 · 재입력 없이 접속 대기를 시작할 수 있습니다.";
            HostPasswordInstruction.Text = changingPassword ? "새 접속 비밀번호 (8자 이상)" : "접속 비밀번호 (8자 이상 · 한 번 저장하면 계속 사용)";
            SaveHostPasswordButton.Content = changingPassword ? "새 비밀번호 저장" : "비밀번호 저장";
            PasswordEditor.Visibility = changingPassword ? Visibility.Visible : Visibility.Collapsed;
            ChangePasswordButton.Visibility = Visibility.Visible;
        }
        PasswordEditor.IsEnabled = host == null && !hostStarting;
        HostPassword.Clear();
    }
    private void ChangeHostPassword(object sender, RoutedEventArgs e)
    {
        if (host != null || hostStarting) { HostStatus("서버를 중지한 뒤 비밀번호를 변경하세요."); return; }
        changingPassword = settings.HasOpaqueIdentity;
        PasswordEditor.Visibility = Visibility.Visible;
        HostPasswordInstruction.Text = changingPassword ? "새 접속 비밀번호 (8자 이상)" : "현재 서버 비밀번호 (기존 비밀번호 유지)";
        SaveHostPasswordButton.Content = changingPassword ? "새 비밀번호 저장" : "보안 등록";
        HostPassword.Focus();
    }
    private async void SaveHostPassword(object sender, RoutedEventArgs e)
    {
        if (host != null || hostStarting) { HostStatus("서버를 중지한 뒤 비밀번호를 변경하세요."); return; }
        try
        {
            if (settings.HasPassword && !settings.HasOpaqueIdentity)
            {
                settings.EnrollOpaqueIdentity(HostPassword.Password);
                HostStatus("기존 비밀번호를 확인했고 첫 지문 등록을 준비했습니다.");
            }
            else
            {
                settings.SetPassword(HostPassword.Password);
                HostStatus("비밀번호 저장됨 · 지금 자동으로 접속 대기를 시작합니다.");
            }
            changingPassword = false;
            UpdatePasswordState();
            await StartHostAsync(automatic: false);
        }
        catch (Exception ex) { HostStatus(ex.Message); }
    }
    private void HostPasswordKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            SaveHostPassword(sender, e);
        }
    }
    private async void StartHost(object sender, RoutedEventArgs e)
    {
        await StartHostAsync(automatic: false);
    }
    private async Task StartHostAsync(bool automatic)
    {
        if (host != null || hostStarting || closing) return;
        settings.SmoothHost = SmoothHostToggle.IsChecked == true;
        hostStarting = true;
        SetHostState(HostUiState.Starting);
        try
        {
            if (automatic && !settings.HasPassword)
            {
                HostStatus("접속 비밀번호를 한 번 저장하면 다음 실행부터 자동으로 접속 대기를 시작합니다.");
                SetHostState(HostUiState.Stopped);
                return;
            }
            if (!int.TryParse(HostPort.Text, out var port) || port is < 1024 or > 65535) throw new ArgumentException("포트 범위: 1024–65535");
            settings.Port = port;
            if (HostPassword.Password.Length > 0) settings.SetPassword(HostPassword.Password);
            if (!settings.HasPassword) throw new InvalidOperationException("비밀번호를 먼저 저장하세요.");
            HostStatus("접속 대기를 시작하는 중…");
            settings.Save();
            host = new RemoteHost(settings); host.Status += HostStatus;
            await host.StartAsync(port, certificate);
            if (closing) { await host.DisposeAsync(); host = null; return; }
            UpdatePasswordState();
            ShowListening(port);
            HostStatus(!settings.HasOpaqueIdentity
                ? "접속 대기 중 · 기존 지문으로 연결 가능 · 비밀번호 기반 첫 등록은 현재 비밀번호를 다시 저장하세요."
                : automatic ? $"자동 접속 대기 중 · 포트 {port}" : $"접속 대기 중 · 포트 {port}");
        }
        catch (Exception ex)
        {
            if (host != null) { await host.DisposeAsync(); host = null; }
            HostStatus((automatic ? "자동 접속 대기 실패: " : "접속 대기 실패: ") + ex.Message);
            SetHostState(HostUiState.Stopped);
        }
        finally { hostStarting = false; }
    }
    private async void StopHost(object sender, RoutedEventArgs e)
    {
        if (hostStarting) { HostStatus("서버를 시작하는 중입니다."); return; }
        SetHostState(HostUiState.Stopping);
        HostStatus("접속 대기를 중지하는 중…");
        if (host != null) { await host.DisposeAsync(); host = null; }
        SetHostState(HostUiState.Stopped);
        UpdatePasswordState();
        HostStatus("서버 중지됨 · 저장된 비밀번호로 다시 시작할 수 있습니다.");
    }
    private void Kick(object sender, RoutedEventArgs e)
    {
        host?.Disconnect();
    }
    private async void Connect(object sender, RoutedEventArgs e)
    {
        if (connecting || client != null) return;
        connecting = true;
        ConnectButton.IsEnabled = false;
        ClientNotice("접속하는 중…");
        var attempt = generation;
        try
        {
            var endpoint = BuildEndpoint();
            var pin = ServerPin.Text.Trim();
            if (pin.Length == 0)
            {
                ClientNotice("비밀번호로 서버 키를 확인하는 중입니다…");
                pin = await PakeBootstrap.AcquireServerFingerprint(endpoint, ClientPassword.Password);
                ServerPin.Text = pin;
            }
            var remote = new RemoteClient(endpoint, pin);
            try { await remote.Login(ClientPassword.Password); } catch { remote.Dispose(); throw; }
            if (attempt != generation) { try { await remote.Logout(); } finally { remote.Dispose(); } return; }
            client = remote; ClientPassword.Clear();
            var previousHost = lastConnection.Address;
            lastConnection.Address = endpoint; lastConnection.Fingerprint = pin;
            if (previousHost != lastConnection.Address) lastConnection.Display = "";
            SaveConnection();
            try { await LoadDisplays(); } catch { await DisconnectClient(); throw; }
            connection = new(); inputs = Channel.CreateBounded<InputEvent>(256);
            clipboardSync = new ClipboardSync(remote, new WindowsFileClipboard(), SessionStatus);
            clipboardSync.AutoEnabled = ClipboardAutoMenu.IsChecked == true;
            inputEnabled = true; InputControlToggle.IsChecked = true;
            ConnectionControls.Visibility = Visibility.Collapsed;
            SetSessionControlsEnabled(true); framesSinceHealth = 0; healthWindowStarted = DateTime.UtcNow; roundTripMilliseconds = -1;
            SessionStatus("연결됨");
            _ = RunConnection(remote, connection.Token);
        }
        catch (Exception ex) { ClientNotice("접속 실패: " + ex.Message, problem: true); }
        finally { connecting = false; ConnectButton.IsEnabled = client == null; }
    }
    private void ClientPasswordKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            Connect(sender, e);
        }
    }
    private string BuildEndpoint()
    {
        var host = Address.Text.Trim();
        if (host.Length == 0) throw new ArgumentException("서버 주소를 입력하세요.");
        if (!int.TryParse(ClientPort.Text, out var port) || port is < 1 or > 65535) throw new ArgumentException("포트 범위: 1–65535");
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    }
    private static (string Host, string Port) SplitEndpoint(string endpoint)
    {
        if (Uri.TryCreate("https://" + endpoint, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            return (uri.Host.Trim('[', ']'), uri.IsDefaultPort ? "45831" : uri.Port.ToString());
        return (endpoint, "45831");
    }
    private async Task RunConnection(RemoteClient remote, CancellationToken ct)
    {
        var reader = inputs!.Reader;
        async Task Frames()
        {
            while (!ct.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var display = selectedDisplay;
                byte[] data;
                RemoteFrame frame;
                try
                {
                    frame = await remote.FrameDetailed(ct, display, frameQuality, frameRate, false);
                    data = frame.Bytes;
                    lastCaptureMilliseconds = frame.CaptureMilliseconds;
                    lastEncodeMilliseconds = frame.EncodeMilliseconds;
                    lastFrameReused = frame.Reused;
                    lastFrameQuality = frame.Quality > 0 ? frame.Quality : frameQuality;
                    lastFrameBytes = data.Length;
                }
                catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
                { await LoadDisplays(); continue; }
                if (display != selectedDisplay) continue;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = new MemoryStream(data); image.EndInit(); image.Freeze();
                ScreenImage.Source = image; renderedDisplay = display;
                UpdateFrameHealth();
                var elapsed = Stopwatch.GetElapsedTime(started);
                var delay = TimeSpan.FromSeconds(1.0 / frameRate) - elapsed;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            }
        }
        async Task SendInputs() { await foreach (var input in reader.ReadAllAsync(ct)) await remote.Input(input, ct); }
        async Task Heartbeat()
        {
            while (!ct.IsCancellationRequested)
            {
                var timer = Stopwatch.StartNew(); await remote.Ping(ct); timer.Stop();
                Interlocked.Exchange(ref roundTripMilliseconds, timer.ElapsedMilliseconds);
                UpdateHealthText();
                await Task.Delay(3000, ct);
            }
        }
        var tasks = new[] { Frames(), SendInputs(), Heartbeat() };
        async Task SyncClipboard() { try { await clipboardSync!.Run(ct); } catch (OperationCanceledException) { } catch (Exception ex) { SessionStatus("클립보드 동기화 오류: " + ex.Message); } }
        var clipboardTask = SyncClipboard();
        try { await await Task.WhenAny(tasks); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { ClientNotice("연결 종료: " + ex.Message, problem: true); }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(client, remote)) await DisconnectClient();
            try { await Task.WhenAll(tasks); } catch { }
            await clipboardTask;
        }
    }
    private async Task DisconnectClient()
    {
        generation++;
        var remote = client; client = null;
        fullscreen?.Close();
        connection?.Cancel(); inputs?.Writer.TryComplete(); ScreenImage.Source = null; selectedDisplay = null;
        SetSessionControlsEnabled(false); ConnectionControls.Visibility = Visibility.Visible; HealthText.Text = "연결 종료"; inputEnabled = false;
        if (remote != null) { try { await remote.Logout(); } catch { } remote.Dispose(); }
    }
    private async void Disconnect(object sender, RoutedEventArgs e) { await DisconnectClient(); ClientNotice("연결 종료됨"); }
    private void SetSessionControlsEnabled(bool enabled)
    {
        SessionToolbar.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        InputControlToggle.IsEnabled = enabled;
        DisplayList.IsEnabled = enabled;
        SessionMenuButton.IsEnabled = enabled;
        EndSessionButton.IsEnabled = enabled;
        if (enabled) HealthText.Text = "측정 중";
    }
    private void CopyFingerprint(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(Fingerprint.Text);
            HostStatus("서버 지문을 복사했습니다.");
        }
        catch (Exception ex) { HostStatus("지문 복사 실패: " + ex.Message); }
    }
    private void Queue(InputEvent input)
    {
        if (!inputEnabled && input.Kind != "release") return;
        if (client != null && inputs != null && !inputs.Writer.TryWrite(input)) { ClientNotice("입력 대기열 초과로 연결을 종료합니다.", problem: true); connection?.Cancel(); }
    }
    private void UpdateFrameHealth()
    {
        framesSinceHealth++;
        if (DateTime.UtcNow - healthWindowStarted < TimeSpan.FromSeconds(1)) return;
        var seconds = Math.Max(0.01, (DateTime.UtcNow - healthWindowStarted).TotalSeconds);
        var fps = framesSinceHealth / seconds; framesSinceHealth = 0; healthWindowStarted = DateTime.UtcNow;
        Dispatcher.InvokeAsync(() => HealthText.Text = $"{fps:F0} fps · {(roundTripMilliseconds < 0 ? "—" : roundTripMilliseconds + " ms")}");
    }
    private void UpdateHealthText()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (framesSinceHealth == 0) HealthText.Text = $"연결됨 · {roundTripMilliseconds} ms";
        });
    }
    private bool Position(MouseEventArgs e)
    {
        if (pasting) return false;
        if (ScreenImage.Source is not BitmapSource image) return false;
        var scale = Math.Min(ScreenImage.ActualWidth / image.PixelWidth, ScreenImage.ActualHeight / image.PixelHeight);
        var w = image.PixelWidth * scale; var h = image.PixelHeight * scale;
        var p = e.GetPosition(ScreenImage);
        var x = (p.X - (ScreenImage.ActualWidth - w) / 2) / w; var y = (p.Y - (ScreenImage.ActualHeight - h) / 2) / h;
        if (!ScreenImage.IsMouseCaptured && (x < 0 || x > 1 || y < 0 || y > 1)) return false;
        Queue(new("move", Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), Display: renderedDisplay)); return true;
    }
    private void Move(object sender, MouseEventArgs e)
    { if (ScreenImage.IsKeyboardFocused && Environment.TickCount64 - lastMove >= 35) { lastMove = Environment.TickCount64; Position(e); } }
    private void MouseDownRemote(object sender, MouseButtonEventArgs e)
    { if (!Position(e)) return; ScreenImage.Focus(); ScreenImage.CaptureMouse(); Queue(new("button", Code: Button(e.ChangedButton), Down: true)); e.Handled = true; }
    private void MouseUpRemote(object sender, MouseButtonEventArgs e)
    { Position(e); Queue(new("button", Code: Button(e.ChangedButton))); ScreenImage.ReleaseMouseCapture(); e.Handled = true; }
    private static int Button(MouseButton button) => button == MouseButton.Right ? 1 : button == MouseButton.Middle ? 2 : 0;
    private void Wheel(object sender, MouseWheelEventArgs e) { Queue(new("wheel", Code: e.Delta)); e.Handled = true; }
    private void KeyDownRemote(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var code = NativeKey(e);
        if (code > 0) Queue(new("key", Code: code, Down: true)); e.Handled = true;
    }
    private static int NativeKey(System.Windows.Input.KeyEventArgs e) => KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key);
    private void InstallSystemShortcutHook()
    {
        if (systemShortcutHook != 0) return;
        systemShortcutHookProc = SystemShortcutHook;
        systemShortcutHook = SetWindowsHookEx(13, systemShortcutHookProc, GetModuleHandle(null), 0);
    }
    private void UninstallSystemShortcutHook()
    {
        if (systemShortcutHook != 0) UnhookWindowsHookEx(systemShortcutHook);
        systemShortcutHook = 0; systemShortcutHookProc = null;
    }
    private nint SystemShortcutHook(int code, nint message, nint data)
    {
        if (code < 0 || !RemoteInputFocused()) return CallNextHookEx(systemShortcutHook, code, message, data);
        var keyboard = Marshal.PtrToStructure<KeyboardHookData>(data);
        var key = (int)keyboard.VirtualKey;
        var down = message is 0x0100 or 0x0104;
        var up = message is 0x0101 or 0x0105;
        if (!down && !up) return CallNextHookEx(systemShortcutHook, code, message, data);
        lock (suppressedSystemKeys)
        {
            if (up && suppressedSystemKeys.Remove(key))
            {
                if (key == 0x12) remoteAltDown = false;
                Queue(new("key", Code: key));
                return 1;
            }
            if (!down) return CallNextHookEx(systemShortcutHook, code, message, data);
            var alt = remoteAltDown || (keyboard.Flags & 0x20) != 0;
            var ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            var shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            var isAlt = key == 0x12;
            var isWin = key is 0x5B or 0x5C;
            var reservedCombo = (alt && (key is 0x09 or 0x1B)) || (ctrl && key == 0x1B) || key == 0x2C;
            if (!isAlt && !isWin && !reservedCombo) return CallNextHookEx(systemShortcutHook, code, message, data);
            if (isAlt) remoteAltDown = true;
            suppressedSystemKeys.Add(key);
            Queue(new("key", Code: key, Down: true));
            return 1;
        }
    }
    private bool RemoteInputFocused() => client != null && inputEnabled && ScreenImage.IsKeyboardFocused && (IsActive || fullscreen?.IsActive == true);
    private nint NativeKeyboard(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (client == null || !ScreenImage.IsKeyboardFocused) return 0;
        // Forward physical keys before WPF/IME consumes VK_HANGUL or VK_PROCESSKEY.
        if (message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            var code = (int)wParam;
            if (code == 0xE5) code = (int)ImmGetVirtualKey(hwnd);
            var down = message is 0x100 or 0x104;
            if (code == 0x7A) { handled = true; if (down && ((long)lParam & (1L << 30)) == 0) ToggleFullscreen(this, new RoutedEventArgs()); return 0; }
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
            handled = true; // Physical key messages already reach the server; do not duplicate translated characters.
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
        var ct = connection.Token; pasting = true; Queue(new("release"));
        try
        {
            await sync.PushAny(ct);
            if (!ReferenceEquals(client, remote) || ct.IsCancellationRequested) return;
            Queue(new("key", Code: 17, Down: true)); Queue(new("key", Code: 86, Down: true));
            Queue(new("key", Code: 86)); Queue(new("key", Code: 17));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SessionStatus("붙여넣기 중단: " + ex.Message); }
        finally { pasting = false; }
    }
    private void KeyUpRemote(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.F11) { e.Handled = true; return; } var code = NativeKey(e); if (code > 0) Queue(new("key", Code: code)); e.Handled = true; }
    private void ToggleKorean(object sender, RoutedEventArgs e) { Queue(new("key", Code: 21, Down: true)); Queue(new("key", Code: 21)); ScreenImage.Focus(); }
    private void InputControlChanged(object sender, RoutedEventArgs e)
    {
        var enabled = InputControlToggle.IsChecked == true;
        if (!enabled) Queue(new("release"));
        inputEnabled = enabled;
        if (client != null) SessionStatus(enabled ? "입력 제어 켜짐" : "입력 제어 꺼짐 · 화면 보기만 가능");
    }
    private void ClipboardAutoChanged(object sender, RoutedEventArgs e)
    {
        var enabled = ClipboardAutoMenu.IsChecked == true;
        if (clipboardSync != null) clipboardSync.AutoEnabled = enabled;
        lastConnection.ClipboardAutoSync = enabled; SaveConnection();
        if (client != null) SessionStatus(enabled ? "클립보드 자동 동기화 켜짐" : "클립보드 자동 동기화 꺼짐");
    }
    private async void ClipboardSend(object sender, RoutedEventArgs e)
    {
        if (clipboardSync is not { } sync || connection is null) return;
        try { if (!await sync.PushAny(connection.Token)) SessionStatus("보낼 텍스트나 파일을 먼저 복사하세요."); }
        catch (Exception ex) { SessionStatus("클립보드 보내기 실패: " + ex.Message); }
    }
    private async void ClipboardReceive(object sender, RoutedEventArgs e)
    {
        if (clipboardSync is not { } sync || connection is null) return;
        try { await sync.Pull(connection.Token); SessionStatus("상대 클립보드를 확인했습니다."); }
        catch (Exception ex) { SessionStatus("클립보드 받기 실패: " + ex.Message); }
    }
    private void QualityMenuChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem selected || !int.TryParse(selected.Tag?.ToString(), out var quality)) return;
        frameQuality = Math.Clamp(quality, 25, 90); lastConnection.FrameQuality = frameQuality; SaveConnection();
        if (client != null) SessionStatus($"화면 품질: {selected.Header}");
    }
    private void FrameRateMenuChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem selected || !int.TryParse(selected.Tag?.ToString(), out var rate) || rate is not (5 or 10 or 15 or 30)) return;
        frameRate = rate; lastConnection.FrameRate = rate; SaveConnection();
        if (client != null) SessionStatus($"최대 프레임 속도: {rate} FPS");
    }
    private void ShowSessionInfo(object sender, RoutedEventArgs e)
    {
        var display = DisplayList.SelectedItem as RemoteDisplay;
        var resolution = ScreenImage.Source is BitmapSource bitmap ? $"{bitmap.PixelWidth}×{bitmap.PixelHeight}" : "수신 전";
        var measured = lastFrameBytes > 0;
        MessageBox.Show(fullscreen ?? this,
            $"대상: {lastConnection.Address}\n모니터: {display?.Name ?? "선택 안 됨"}\n수신 해상도: {resolution}\n요청 화질: {frameQuality}\n서버 화질: {(measured && lastFrameQuality > 0 ? lastFrameQuality.ToString() : "측정 전")}\n최대 프레임 속도: {frameRate} FPS\n캡처: {(measured ? $"{lastCaptureMilliseconds:0.0} ms" : "측정 전")}\n압축: {(measured ? $"{lastEncodeMilliseconds:0.0} ms" : "측정 전")}\n프레임 크기: {(measured ? $"{lastFrameBytes / 1024.0:0} KB" : "측정 전")}\n화면 재사용: {(measured ? (lastFrameReused ? "예" : "아니오") : "측정 전")}\n왕복 지연: {(roundTripMilliseconds < 0 ? "측정 중" : roundTripMilliseconds + " ms")}\n입력 제어: {(inputEnabled ? "켜짐" : "꺼짐")}\n클립보드 자동 동기화: {(ClipboardAutoMenu.IsChecked == true ? "켜짐" : "꺼짐")}",
            "세션 정보", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void SaveConnection()
    {
        try { lastConnection.Save(); }
        catch (Exception ex)
        {
            var message = "연결 정보 저장 실패: " + ex.Message;
            if (client == null) ClientNotice(message, problem: true); else SessionStatus(message);
        }
    }
    private void OpenSessionMenu(object sender, RoutedEventArgs e)
    {
        MarkChoice(0, frameQuality);
        MarkChoice(1, frameRate);
        SessionMenu.PlacementTarget = SessionMenuButton;
        SessionMenu.IsOpen = true;
    }
    private void MarkChoice(int index, int selected)
    {
        if (SessionMenu.Items[index] is not System.Windows.Controls.MenuItem parent) return;
        foreach (var item in parent.Items)
        {
            if (item is not System.Windows.Controls.MenuItem choice) continue;
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
        DisplayList.ItemsSource = displays;
        DisplayList.SelectedItem = displays.FirstOrDefault(d => d.Id == wanted) ?? displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
    }
    private async void RefreshDisplays(object sender, RoutedEventArgs e) { try { await LoadDisplays(); } catch (Exception ex) { SessionStatus(ex.Message); } }
    private void DisplayChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DisplayList.SelectedItem is not RemoteDisplay display) return;
        Queue(new("release")); ScreenImage.Source = null; renderedDisplay = null;
        selectedDisplay = display.Id; lastConnection.Display = display.Id;
        if (client != null) SaveConnection();
    }
    private void FullscreenKey(object sender, System.Windows.Input.KeyEventArgs e)
    { if (e.Key == Key.F11 && (fullscreen != null || ClientTab.IsSelected)) { e.Handled = true; ToggleFullscreen(sender, e); } }
    private void ToggleFullscreen(object sender, RoutedEventArgs e)
    {
        if (fullscreen != null) { fullscreen.Close(); return; }
        Queue(new("release"));
        ClientTab.Content = null; ConnectionControls.Visibility = Visibility.Collapsed; ClientPanel.Margin = new Thickness(0);
        // Keep this a normal fullscreen window. A Topmost window would hide local dialogs and other client windows.
        var window = new Window { Title = "Personal Remote Desktop · F11 복귀", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = System.Windows.Media.Brushes.Black, Owner = this, Content = ClientPanel };
        // Open on the same local monitor as the main window before maximizing.
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = Left; window.Top = Top; window.Width = ActualWidth; window.Height = ActualHeight;
        fullscreen = window;
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(NativeKeyboard);
        window.PreviewKeyDown += FullscreenKey;
        window.Deactivated += (_, _) => Queue(new("release"));
        window.Closed += (_, _) => { Queue(new("release")); window.Content = null; ClientTab.Content = ClientPanel; ClientPanel.Margin = new Thickness(16); ConnectionControls.Visibility = client is null ? Visibility.Visible : Visibility.Collapsed; fullscreen = null; ScreenImage.Focus(); };
        window.Show(); window.WindowState = WindowState.Maximized; ScreenImage.Focus();
    }
    private void ReleaseInput(object sender, KeyboardFocusChangedEventArgs e)
    {
        lock (suppressedSystemKeys) { remoteAltDown = false; suppressedSystemKeys.Clear(); }
        Queue(new("release"));
    }
}
