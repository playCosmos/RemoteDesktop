using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace RemoteDesktop;

public partial class MainWindow
{
    private sealed class HostSession
    {
        private readonly MainWindow window;
        private readonly HostSettings settings = HostSettings.Load();
        private readonly X509Certificate2 certificate = HostSettings.Certificate();
        private RemoteHost? host;
        private bool hostStarting;
        private bool changingPassword;
        private bool suppressHostPasswordReveal;
        private System.Windows.Threading.DispatcherTimer? noticeTimer;
        private System.Windows.Threading.DispatcherTimer? sessionClock;
        private System.Windows.Threading.DispatcherTimer? addressRefresh;
        private bool addressWatch;
        private System.Windows.Controls.Button? copiedButton;
        private string copiedIdle = "";
        private System.Windows.Threading.DispatcherTimer? copiedTimer;
        private HostUiState hostState = HostUiState.Stopped;
        private bool hostQuiet;
        public bool Closing { get; set; }
        public bool HasClient => host?.HasSession == true;
        public string? ConnectedPeer => host?.SessionPeer;

        public HostSession(MainWindow window) => this.window = window;

        public void Bind()
        {
            window.SmoothHostToggle.IsChecked = settings.UseSmoothHost;
            window.HostPort.Text = settings.Port.ToString();
            UpdatePasswordState();
            window.Fingerprint.Text = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            window.FingerprintSummary.Text = UiText.ShortSecret(window.Fingerprint.Text);
            RefreshAddresses();
            if (!addressWatch)
            {
                NetworkChange.NetworkAddressChanged += NetworkChanged;
                addressWatch = true;
            }
        }

        public void ToggleFingerprint(object sender, RoutedEventArgs e)
        {
            var open = window.Fingerprint.Visibility != Visibility.Visible;
            window.Fingerprint.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            window.ToggleFingerprintButton.Content = open ? "지문 숨기기" : "지문 보기";
        }

        public void RefreshAddresses()
        {
            RefreshAddresses(announce: false);
        }

        private void RefreshAddresses(bool announce)
        {
            try
            {
                var port = int.TryParse(window.HostPort.Text, out var parsed) && parsed is >= 1 and <= 65535 ? parsed : settings.Port;
                var endpoints = HostAddresses.Endpoints(port);
                var previous = (window.AddressList.ItemsSource as System.Collections.Generic.IEnumerable<string>)?.ToArray() ?? [];
                var changed = !HostAddresses.Same(previous, endpoints);
                RestoreCopiedButton(null, EventArgs.Empty);
                window.AddressList.ItemsSource = endpoints;
                window.AddressEmpty.Visibility = endpoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                if (announce && changed) window.HostNotice(UiText.AddressesChanged());
            }
            catch (Exception ex) { window.HostNotice(UiText.Explain(ex, "네트워크 주소를 읽지 못했습니다."), problem: true); }
        }

        private void NetworkChanged(object? sender, EventArgs e)
        {
            try { if (addressWatch) window.Dispatcher.InvokeAsync(ScheduleAddressRefresh); }
            catch (Exception) { }
        }

        private void ScheduleAddressRefresh()
        {
            if (!addressWatch) return;
            addressRefresh ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            addressRefresh.Stop();
            addressRefresh.Tick -= ApplyAddressRefresh;
            addressRefresh.Tick += ApplyAddressRefresh;
            addressRefresh.Start();
        }

        private void ApplyAddressRefresh(object? sender, EventArgs e)
        {
            addressRefresh?.Stop();
            RefreshAddresses(announce: true);
        }

        public void CopyAddress(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button { Tag: string endpoint } || endpoint.Length == 0) return;
            try
            {
                System.Windows.Clipboard.SetText(endpoint);
                if (sender is System.Windows.Controls.Button button) ShowCopied(button, "복사");
                window.HostNotice(endpoint + " 복사됨 · 상대 PC의 주소 칸에 붙여 넣으세요.");
            }
            catch (Exception ex) { window.HostNotice("주소 복사 실패: " + UiText.Explain(ex, "클립보드를 사용할 수 없습니다."), problem: true); }
        }

        public async Task StartIfConfigured()
        {
            if (settings.HasPassword) await StartHostAsync(automatic: true);
            else
            {
                SetHostState(HostUiState.Stopped);
                window.HostStatus("서버 비밀번호를 한 번 설정하면 다음 실행부터 자동으로 접속 대기를 시작합니다.");
            }
        }

        public async Task DisposeAsync()
        {
            if (addressWatch)
            {
                NetworkChange.NetworkAddressChanged -= NetworkChanged;
                addressWatch = false;
            }
            addressRefresh?.Stop();
            if (host != null) await host.DisposeAsync();
            certificate.Dispose();
        }

        public void SmoothHostChanged(object sender, RoutedEventArgs e)
        {
            if (!window.IsLoaded || window.SmoothHostToggle.IsChecked is not bool enabled) return;
            settings.SmoothHost = enabled;
            try { settings.Save(); }
            catch (Exception ex) { window.HostStatus(UiText.Explain(ex, "끊김 줄이기 설정을 저장하지 못했습니다.")); }
        }

        private enum HostUiState { Stopped, Starting, Listening, Stopping }

        private void SetHostState(HostUiState state)
        {
            hostState = state;
            window.StartButton.Content = state switch
            {
                HostUiState.Starting => "접속 대기 시작 중…",
                HostUiState.Stopping => "접속 대기 중지 중…",
                _ => "접속 대기 시작"
            };
            window.StartButton.IsEnabled = state == HostUiState.Stopped;
            window.StopButton.IsEnabled = state == HostUiState.Listening;
            window.KickButton.IsEnabled = state == HostUiState.Listening && host?.HasSession == true;
            window.StartButton.Visibility = state is HostUiState.Stopped or HostUiState.Starting ? Visibility.Visible : Visibility.Collapsed;
            window.StopButton.Visibility = state is HostUiState.Listening or HostUiState.Stopping ? Visibility.Visible : Visibility.Collapsed;
            window.KickButton.Visibility = window.KickButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            window.HostPort.IsEnabled = window.ChangePasswordButton.IsEnabled = state == HostUiState.Stopped;
            window.SmoothHostToggle.IsEnabled = state == HostUiState.Stopped;
            ApplyPresence();
        }

        private void ApplyPresence()
        {
            var occupied = hostState == HostUiState.Listening && host?.HasSession == true;
            var seen = host?.SessionLastSeenUtc;
            hostQuiet = occupied && seen != null && UiText.HostShowsQuiet(DateTime.UtcNow - seen.Value);
            var live = hostState is HostUiState.Starting or HostUiState.Listening;
            window.HostSetup.Visibility = UiText.ShowHostSetup(hostState != HostUiState.Stopped) ? Visibility.Visible : Visibility.Collapsed;
            window.HostCardTitle.Text = UiText.HostCardTitle(occupied, live, ConnectedPeer, hostQuiet);
            window.ShareTitle.Text = UiText.ShareTitle(occupied, hostQuiet);
            window.ShareHint.Text = UiText.ShareHint(occupied, hostQuiet);
            window.KickButton.Style = occupied ? (System.Windows.Style)window.FindResource("PrimaryButton") : null;
            window.StopButton.Style = hostState is HostUiState.Listening or HostUiState.Stopping && !occupied ? (System.Windows.Style)window.FindResource("PrimaryButton") : null;
            window.HostStateMark.Fill = (System.Windows.Media.Brush)window.FindResource(UiText.HostPresence(occupied, live, hostQuiet));
        }

        private void ShowListening(int port)
        {
            SetHostState(HostUiState.Listening);
            if (window.HostPort.Text.Trim() != port.ToString()) window.HostPort.Text = port.ToString();
            else RefreshAddresses();
        }

        public void ShowNotice(string text, bool problem)
        {
            window.HostNoticeText.Text = text;
            window.HostNoticeText.Foreground = problem
                ? (System.Windows.Media.Brush)window.FindResource("DangerBrush")
                : (System.Windows.Media.Brush)window.FindResource("AccentBrush");
            window.HostNoticeText.Visibility = Visibility.Visible;
            noticeTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            noticeTimer.Stop();
            noticeTimer.Tick -= ClearNotice;
            noticeTimer.Tick += ClearNotice;
            noticeTimer.Start();
        }

        private void ClearNotice(object? sender, EventArgs e)
        {
            noticeTimer?.Stop();
            window.HostNoticeText.Text = "";
            window.HostNoticeText.Visibility = Visibility.Collapsed;
        }

        public void SyncConnectedClock()
        {
            var started = host?.SessionStartedUtc;
            if (started == null)
            {
                sessionClock?.Stop();
                window.HostSessionText.Text = "";
                window.HostSessionText.Visibility = Visibility.Collapsed;
            }
            else
            {
                window.HostSessionText.Text = UiText.ConnectedFor(DateTime.UtcNow - started.Value);
                window.HostSessionText.Visibility = Visibility.Visible;
                sessionClock ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                sessionClock.Stop();
                sessionClock.Tick -= TickConnectedClock;
                sessionClock.Tick += TickConnectedClock;
                sessionClock.Start();
            }
            ApplyPresence();
        }

        private void TickConnectedClock(object? sender, EventArgs e)
        {
            var started = host?.SessionStartedUtc;
            if (started == null)
            {
                sessionClock?.Stop();
                window.HostSessionText.Text = "";
                window.HostSessionText.Visibility = Visibility.Collapsed;
                return;
            }
            window.HostSessionText.Text = UiText.ConnectedFor(DateTime.UtcNow - started.Value);
            var seen = host?.SessionLastSeenUtc;
            var quiet = seen != null && UiText.HostShowsQuiet(DateTime.UtcNow - seen.Value);
            if (quiet != hostQuiet) ApplyPresence();
        }

        private void UpdatePasswordState()
        {
            if (!settings.HasPassword)
            {
                window.PasswordState.Text = "접속 비밀번호를 한 번 설정하세요.";
                window.HostPasswordInstruction.Text = "접속 비밀번호 (8자 이상 · 한 번 저장하면 계속 사용)";
                window.SaveHostPasswordButton.Content = "비밀번호 저장";
                window.PasswordEditor.Visibility = Visibility.Visible;
                window.ChangePasswordButton.Visibility = Visibility.Collapsed;
            }
            else if (!settings.HasOpaqueIdentity)
            {
                window.PasswordState.Text = "비밀번호는 저장되어 있습니다. 비밀번호만으로 접속하려면 현재 비밀번호를 한 번 더 입력하세요.";
                window.HostPasswordInstruction.Text = "현재 저장된 서버 비밀번호 (변경되지 않음)";
                window.SaveHostPasswordButton.Content = "보안 등록";
                window.PasswordEditor.Visibility = Visibility.Visible;
                window.ChangePasswordButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                window.PasswordState.Text = "비밀번호 저장됨 · 재입력 없이 접속 대기를 시작할 수 있습니다.";
                window.HostPasswordInstruction.Text = changingPassword ? "새 접속 비밀번호 (8자 이상)" : "접속 비밀번호 (8자 이상 · 한 번 저장하면 계속 사용)";
                window.SaveHostPasswordButton.Content = changingPassword ? "새 비밀번호 저장" : "비밀번호 저장";
                window.PasswordEditor.Visibility = changingPassword ? Visibility.Visible : Visibility.Collapsed;
                window.ChangePasswordButton.Visibility = Visibility.Visible;
            }
            window.PasswordEditor.IsEnabled = host == null && !hostStarting;
            ClearHostPassword();
        }

        public void ChangeHostPassword(object sender, RoutedEventArgs e)
        {
            if (host != null || hostStarting) { window.HostStatus("서버를 중지한 뒤 비밀번호를 변경하세요."); return; }
            changingPassword = settings.HasOpaqueIdentity;
            window.PasswordEditor.Visibility = Visibility.Visible;
            window.HostPasswordInstruction.Text = changingPassword ? "새 접속 비밀번호 (8자 이상)" : "현재 서버 비밀번호 (기존 비밀번호 유지)";
            window.SaveHostPasswordButton.Content = changingPassword ? "새 비밀번호 저장" : "보안 등록";
            FocusHostPassword();
        }

        public async void SaveHostPassword(object sender, RoutedEventArgs e)
        {
            if (host != null || hostStarting) { window.HostStatus("서버를 중지한 뒤 비밀번호를 변경하세요."); return; }
            try
            {
                if (settings.HasPassword && !settings.HasOpaqueIdentity)
                {
                    settings.EnrollOpaqueIdentity(CurrentHostPassword());
                    window.HostStatus("기존 비밀번호를 확인했고 첫 지문 등록을 준비했습니다.");
                }
                else
                {
                    settings.SetPassword(CurrentHostPassword());
                    window.HostStatus("비밀번호 저장됨 · 지금 자동으로 접속 대기를 시작합니다.");
                }
                changingPassword = false;
                UpdatePasswordState();
                await StartHostAsync(automatic: false);
            }
            catch (Exception ex) { window.HostStatus(UiText.Explain(ex, "비밀번호를 저장하지 못했습니다.")); }
        }

        public void HostPasswordKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                e.Handled = true;
                SaveHostPassword(sender, e);
            }
        }

        public void HostPasswordRevealChanged(object sender, RoutedEventArgs e)
        {
            if (suppressHostPasswordReveal) return;
            var shown = window.HostPasswordReveal.IsChecked == true;
            window.HostPasswordReveal.Content = UiText.PasswordReveal(shown);
            window.HostPasswordReveal.ToolTip = UiText.PasswordRevealTip(shown);
            if (shown)
            {
                suppressHostPasswordReveal = true;
                window.HostPasswordVisible.Text = window.HostPassword.Password;
                suppressHostPasswordReveal = false;
                window.HostPassword.Visibility = Visibility.Collapsed;
                window.HostPasswordVisible.Visibility = Visibility.Visible;
                window.HostPasswordVisible.CaretIndex = window.HostPasswordVisible.Text.Length;
                if (window.HostPasswordVisible.IsEnabled) window.HostPasswordVisible.Focus();
            }
            else
            {
                window.HostPassword.Password = window.HostPasswordVisible.Text;
                suppressHostPasswordReveal = true;
                window.HostPasswordVisible.Clear();
                suppressHostPasswordReveal = false;
                window.HostPasswordVisible.Visibility = Visibility.Collapsed;
                window.HostPassword.Visibility = Visibility.Visible;
                if (window.HostPassword.IsEnabled) window.HostPassword.Focus();
            }
        }

        public void HostPasswordVisibleChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressHostPasswordReveal || window.HostPasswordReveal.IsChecked != true) return;
            window.HostPassword.Password = window.HostPasswordVisible.Text;
        }

        private string CurrentHostPassword()
        {
            if (window.HostPasswordReveal.IsChecked == true)
                window.HostPassword.Password = window.HostPasswordVisible.Text;
            return window.HostPassword.Password;
        }

        private void FocusHostPassword()
        {
            if (window.HostPasswordReveal.IsChecked == true) window.HostPasswordVisible.Focus();
            else window.HostPassword.Focus();
        }

        private void ClearHostPassword()
        {
            suppressHostPasswordReveal = true;
            window.HostPassword.Clear();
            window.HostPasswordVisible.Clear();
            window.HostPasswordReveal.IsChecked = false;
            window.HostPasswordReveal.Content = UiText.PasswordReveal(false);
            window.HostPasswordReveal.ToolTip = UiText.PasswordRevealTip(false);
            window.HostPasswordVisible.Visibility = Visibility.Collapsed;
            window.HostPassword.Visibility = Visibility.Visible;
            suppressHostPasswordReveal = false;
        }

        public async void StartHost(object sender, RoutedEventArgs e) => await StartHostAsync(automatic: false);

        private async Task StartHostAsync(bool automatic)
        {
            if (host != null || hostStarting || Closing) return;
            settings.SmoothHost = window.SmoothHostToggle.IsChecked == true;
            hostStarting = true;
            SetHostState(HostUiState.Starting);
            try
            {
                if (automatic && !settings.HasPassword)
                {
                    window.HostStatus("접속 비밀번호를 한 번 저장하면 다음 실행부터 자동으로 접속 대기를 시작합니다.");
                    SetHostState(HostUiState.Stopped);
                    return;
                }
                if (!int.TryParse(window.HostPort.Text, out var port) || port is < 1024 or > 65535) throw new ArgumentException("포트 범위: 1024–65535");
                settings.Port = port;
                var typedPassword = CurrentHostPassword();
                if (typedPassword.Length > 0) settings.SetPassword(typedPassword);
                if (!settings.HasPassword) throw new InvalidOperationException("비밀번호를 먼저 저장하세요.");
                window.HostStatus("접속 대기를 시작하는 중…");
                settings.Save();
                host = new RemoteHost(settings);
                host.Status += window.HostStatus;
                await host.StartAsync(port, certificate);
                if (Closing) { await host.DisposeAsync(); host = null; return; }
                UpdatePasswordState();
                ShowListening(port);
                window.HostStatus(!settings.HasOpaqueIdentity
                    ? "접속 대기 중 · 포트 " + port + ". 비밀번호만 접속은 현재 비밀번호를 한 번 더 저장하면 켜집니다."
                    : automatic ? "자동 접속 대기 중 · 포트 " + port : "접속 대기 중 · 포트 " + port);
            }
            catch (Exception ex)
            {
                if (host != null) { await host.DisposeAsync(); host = null; }
                window.HostStatus((automatic ? "자동 접속 대기 실패: " : "접속 대기 실패: ") + UiText.Explain(ex, "포트와 비밀번호를 확인하세요."));
                SetHostState(HostUiState.Stopped);
            }
            finally { hostStarting = false; }
        }

        public async void StopHost(object sender, RoutedEventArgs e)
        {
            if (hostStarting) { window.HostStatus("서버를 시작하는 중입니다."); return; }
            SetHostState(HostUiState.Stopping);
            window.HostStatus("접속 대기를 중지하는 중…");
            if (host != null) { await host.DisposeAsync(); host = null; }
            SetHostState(HostUiState.Stopped);
            UpdatePasswordState();
            window.HostStatus("서버 중지됨 · 저장된 비밀번호로 다시 시작할 수 있습니다.");
        }

        public void Kick(object sender, RoutedEventArgs e) => host?.Disconnect();

        public void CopyFingerprint(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(window.Fingerprint.Text);
                ShowCopied(window.FingerprintCopyButton, "지문 복사");
                window.HostNotice("서버 지문을 복사했습니다.");
            }
            catch (Exception ex) { window.HostNotice("지문 복사 실패: " + UiText.Explain(ex, "클립보드를 사용할 수 없습니다."), problem: true); }
        }

        private void ShowCopied(System.Windows.Controls.Button button, string idle)
        {
            if (copiedButton != null && !ReferenceEquals(copiedButton, button)) copiedButton.Content = copiedIdle;
            copiedButton = button;
            copiedIdle = idle;
            button.Content = UiText.CopiedLabel(idle, true);
            copiedTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            copiedTimer.Stop();
            copiedTimer.Tick -= RestoreCopiedButton;
            copiedTimer.Tick += RestoreCopiedButton;
            copiedTimer.Start();
        }

        private void RestoreCopiedButton(object? sender, EventArgs e)
        {
            copiedTimer?.Stop();
            if (copiedButton != null) copiedButton.Content = copiedIdle;
            copiedButton = null;
        }
    }
}
