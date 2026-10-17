using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Diagnostics;
using System.ComponentModel;

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
        private CancellationTokenSource? hostStart;
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
        public bool Listening => hostState == HostUiState.Listening;

        public HostSession(MainWindow window) => this.window = window;

        public void Bind()
        {
            window.SmoothHostToggle.IsChecked = settings.UseSmoothHost;
            window.HostPort.Text = settings.Port.ToString();
            UpdatePasswordState();
            window.Fingerprint.Text = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            window.FingerprintSummary.Text = UiText.ShortSecret(window.Fingerprint.Text);
            RefreshAddresses();
            RefreshPlacement();
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
                window.HostNotice(UiText.AddressCopied(endpoint, hostState == HostUiState.Listening));
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
            try { hostStart?.Cancel(); } catch (ObjectDisposedException) { }
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
            window.NoteTray();
            window.StartButton.Content = state switch
            {
                HostUiState.Starting => UiText.HostStartLabel(true),
                HostUiState.Stopping => "접속 대기 중지 중…",
                _ => UiText.HostStartLabel(false)
            };
            window.StartButton.IsEnabled = state is HostUiState.Stopped or HostUiState.Starting;
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
            var accepting = hostState == HostUiState.Listening;
            window.ShareTitle.Text = UiText.ShareTitle(occupied, hostQuiet, accepting);
            window.ShareHint.Text = UiText.ShareHint(occupied, hostQuiet, accepting);
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
            if (problem)
            {
                noticeTimer?.Stop();
                return;
            }
            noticeTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            noticeTimer.Stop();
            noticeTimer.Tick -= ClearNotice;
            noticeTimer.Tick += ClearNotice;
            noticeTimer.Start();
        }

        private void ClearNotice(object? sender, EventArgs e) => DismissNotice();

        private void DismissNotice()
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
            window.HostPasswordConfirmPanel.Visibility = !settings.HasPassword || (settings.HasOpaqueIdentity && changingPassword) ? Visibility.Visible : Visibility.Collapsed;
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
            window.HostPasswordConfirmPanel.Visibility = changingPassword ? Visibility.Visible : Visibility.Collapsed;
            FocusHostPassword();
        }

        public async void SaveHostPassword(object sender, RoutedEventArgs e)
        {
            if (host != null || hostStarting) { window.HostStatus("서버를 중지한 뒤 비밀번호를 변경하세요."); return; }
            var confirming = settings.HasPassword && !settings.HasOpaqueIdentity;
            var setup = UiText.CheckHostSetup(window.HostPort.Text, CurrentHostPassword(), passwordRequired: true, confirming, CurrentHostPasswordConfirm(), matchRequired: !confirming);
            if (setup.Problem != null)
            {
                window.HostNotice(setup.Problem, problem: true);
                MarkHostField(setup.Field);
                return;
            }
            MarkHostField("");
            DismissNotice();
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
            catch (Exception ex) { window.HostNotice(UiText.Explain(ex, "비밀번호를 저장하지 못했습니다."), problem: true); }
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
                window.HostPasswordConfirmVisible.Text = window.HostPasswordConfirm.Password;
                suppressHostPasswordReveal = false;
                window.HostPassword.Visibility = Visibility.Collapsed;
                window.HostPasswordVisible.Visibility = Visibility.Visible;
                window.HostPasswordConfirm.Visibility = Visibility.Collapsed;
                window.HostPasswordConfirmVisible.Visibility = Visibility.Visible;
                window.HostPasswordVisible.CaretIndex = window.HostPasswordVisible.Text.Length;
                if (window.HostPasswordVisible.IsEnabled) window.HostPasswordVisible.Focus();
            }
            else
            {
                suppressHostPasswordReveal = true;
                window.HostPassword.Password = window.HostPasswordVisible.Text;
                window.HostPasswordConfirm.Password = window.HostPasswordConfirmVisible.Text;
                window.HostPasswordVisible.Clear();
                window.HostPasswordConfirmVisible.Clear();
                suppressHostPasswordReveal = false;
                window.HostPasswordVisible.Visibility = Visibility.Collapsed;
                window.HostPassword.Visibility = Visibility.Visible;
                window.HostPasswordConfirmVisible.Visibility = Visibility.Collapsed;
                window.HostPasswordConfirm.Visibility = Visibility.Visible;
                if (window.HostPassword.IsEnabled) window.HostPassword.Focus();
            }
        }

        public void HostPasswordVisibleChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressHostPasswordReveal || window.HostPasswordReveal.IsChecked != true) return;
            window.HostPassword.Password = window.HostPasswordVisible.Text;
        }

        public void HostPasswordConfirmVisibleChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressHostPasswordReveal || window.HostPasswordReveal.IsChecked != true) return;
            window.HostPasswordConfirm.Password = window.HostPasswordConfirmVisible.Text;
        }

        private string CurrentHostPassword()
        {
            if (window.HostPasswordReveal.IsChecked == true)
                window.HostPassword.Password = window.HostPasswordVisible.Text;
            return window.HostPassword.Password;
        }

        private string CurrentHostPasswordConfirm()
        {
            if (window.HostPasswordReveal.IsChecked == true)
                window.HostPasswordConfirm.Password = window.HostPasswordConfirmVisible.Text;
            return window.HostPasswordConfirm.Password;
        }

        private void FocusHostPassword()
        {
            if (window.HostPasswordReveal.IsChecked == true) window.HostPasswordVisible.Focus();
            else window.HostPassword.Focus();
        }

        private void FocusHostSetup(string field)
        {
            if (field == "port") window.HostPort.Focus();
            else if (field == "confirm") FocusHostPasswordConfirm();
            else FocusHostPassword();
        }

        private void FocusHostPasswordConfirm()
        {
            if (window.HostPasswordReveal.IsChecked == true) window.HostPasswordConfirmVisible.Focus();
            else window.HostPasswordConfirm.Focus();
        }

        public void Edited(FrameworkElement element)
        {
            if (Equals(element.Tag, UiText.InvalidField)) element.Tag = null;
        }

        public void PasswordEdited()
        {
            if (suppressHostPasswordReveal) return;
            Edited(window.HostPassword);
            Edited(window.HostPasswordVisible);
        }

        public void ConfirmEdited()
        {
            if (suppressHostPasswordReveal) return;
            Edited(window.HostPasswordConfirm);
            Edited(window.HostPasswordConfirmVisible);
        }

        private void MarkHostField(string field)
        {
            MarkField(window.HostPort, field == "port");
            MarkField(window.HostPassword, field == "password");
            MarkField(window.HostPasswordVisible, field == "password");
            MarkField(window.HostPasswordConfirm, field == "confirm");
            MarkField(window.HostPasswordConfirmVisible, field == "confirm");
            if (field.Length > 0) FocusHostSetup(field);
        }

        private static void MarkField(FrameworkElement element, bool invalid) => element.Tag = invalid ? UiText.InvalidField : null;

        private void ClearHostPassword()
        {
            suppressHostPasswordReveal = true;
            window.HostPassword.Clear();
            window.HostPasswordVisible.Clear();
            window.HostPasswordConfirm.Clear();
            window.HostPasswordConfirmVisible.Clear();
            window.HostPasswordReveal.IsChecked = false;
            window.HostPasswordReveal.Content = UiText.PasswordReveal(false);
            window.HostPasswordReveal.ToolTip = UiText.PasswordRevealTip(false);
            window.HostPasswordVisible.Visibility = Visibility.Collapsed;
            window.HostPassword.Visibility = Visibility.Visible;
            window.HostPasswordConfirmVisible.Visibility = Visibility.Collapsed;
            window.HostPasswordConfirm.Visibility = Visibility.Visible;
            window.HostPassword.Tag = null;
            window.HostPasswordVisible.Tag = null;
            window.HostPasswordConfirm.Tag = null;
            window.HostPasswordConfirmVisible.Tag = null;
            suppressHostPasswordReveal = false;
        }

        public void StartHost(object sender, RoutedEventArgs e)
        {
            if (hostStarting)
            {
                CancelHostStart();
                return;
            }
            _ = StartHostAsync(automatic: false);
        }

        private void CancelHostStart()
        {
            try { hostStart?.Cancel(); } catch (ObjectDisposedException) { }
            window.HostStatus(UiText.HostStartCancelled());
        }

        private async Task StartHostAsync(bool automatic)
        {
            if (host != null || hostStarting || Closing) return;
            if (automatic && !settings.HasPassword)
            {
                window.HostStatus("접속 비밀번호를 한 번 저장하면 다음 실행부터 자동으로 접속 대기를 시작합니다.");
                SetHostState(HostUiState.Stopped);
                return;
            }
            var typedPassword = CurrentHostPassword();
            var confirmingExisting = settings.HasPassword && !settings.HasOpaqueIdentity;
            var setup = UiText.CheckHostSetup(window.HostPort.Text, typedPassword, passwordRequired: !settings.HasPassword, confirmingExisting, CurrentHostPasswordConfirm(), matchRequired: !confirmingExisting && typedPassword.Length > 0);
            if (setup.Problem != null)
            {
                window.HostNotice(setup.Problem, problem: true);
                MarkHostField(setup.Field);
                return;
            }
            MarkHostField("");
            DismissNotice();
            settings.SmoothHost = window.SmoothHostToggle.IsChecked == true;
            hostStart?.Dispose();
            var start = new CancellationTokenSource();
            hostStart = start;
            hostStarting = true;
            SetHostState(HostUiState.Starting);
            try
            {
                if (!int.TryParse(window.HostPort.Text, out var port) || port is < 1024 or > 65535) throw new ArgumentException("포트 범위: 1024–65535");
                settings.Port = port;
                var password = typedPassword;
                window.HostStatus("접속 대기를 시작하는 중…");
                await Task.Run(() =>
                {
                    start.Token.ThrowIfCancellationRequested();
                    if (password.Length > 0) settings.SetPassword(password);
                    start.Token.ThrowIfCancellationRequested();
                    if (!settings.HasPassword) throw new InvalidOperationException("비밀번호를 먼저 저장하세요.");
                    settings.Save();
                }, start.Token);
                host = new RemoteHost(settings);
                host.Status += window.HostStatus;
                await host.StartAsync(port, certificate);
                if (Closing || start.IsCancellationRequested)
                {
                    await host.DisposeAsync();
                    host = null;
                    if (!Closing)
                    {
                        SetHostState(HostUiState.Stopped);
                        window.HostStatus(UiText.HostStartCancelled());
                    }
                    return;
                }
                UpdatePasswordState();
                ShowListening(port);
                window.HostStatus(!settings.HasOpaqueIdentity
                    ? "접속 대기 중 · 포트 " + port + ". 비밀번호만 접속은 현재 비밀번호를 한 번 더 저장하면 켜집니다."
                    : automatic ? "자동 접속 대기 중 · 포트 " + port : "접속 대기 중 · 포트 " + port);
                RequestFirewall(port);
            }
            catch (OperationCanceledException)
            {
                if (host != null) { await host.DisposeAsync(); host = null; }
                if (!Closing)
                {
                    SetHostState(HostUiState.Stopped);
                    window.HostStatus(UiText.HostStartCancelled());
                }
            }
            catch (Exception ex)
            {
                if (host != null) { await host.DisposeAsync(); host = null; }
                window.HostStatus((automatic ? "자동 접속 대기 실패: " : "접속 대기 실패: ") + UiText.Explain(ex, "포트와 비밀번호를 확인하세요."));
                SetHostState(HostUiState.Stopped);
                var field = UiText.HostFailureField(ex);
                if (field.Length > 0) MarkHostField(field);
            }
            finally
            {
                hostStarting = false;
                if (ReferenceEquals(hostStart, start)) hostStart = null;
                start.Dispose();
            }
        }

        private void RequestFirewall(int port)
        {
            var program = Environment.ProcessPath;
            _ = Task.Run(() =>
            {
                var notice = HostFirewall.Ensure(port, program);
                if (notice.Length == 0 || Closing) return;
                try { window.HostNotice(notice); }
                catch (InvalidOperationException) { }
            });
        }

        public async void StopHost(object sender, RoutedEventArgs e)
        {
            if (hostStarting) { CancelHostStart(); return; }
            var peer = ConnectedPeer;
            SetHostState(HostUiState.Stopping);
            window.HostStatus("접속 대기를 중지하는 중…");
            if (host != null) { await host.DisposeAsync(); host = null; }
            SetHostState(HostUiState.Stopped);
            UpdatePasswordState();
            window.HostStatus(UiText.HostStopped(peer));
        }

        public void Kick(object sender, RoutedEventArgs e) => host?.Disconnect(UiText.SessionEndKind.Kicked);

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

        private bool installBusy;
        private bool uninstallArmed;

        private void RefreshPlacement()
        {
            var scope = CurrentScope();
            var published = AppInstall.CanInstall(Environment.ProcessPath);
            window.PlacementTitle.Text = UiText.PlacementTitle(scope);
            window.PlacementBody.Text = UiText.PlacementBody(scope, published);
            window.InstallAutostart.Visibility = AppInstall.ShowAutostart(scope) ? Visibility.Visible : Visibility.Collapsed;
            window.InstallUserButton.Visibility = AppInstall.ShowUserInstall(scope) ? Visibility.Visible : Visibility.Collapsed;
            window.InstallMachineButton.Visibility = AppInstall.ShowMachineInstall(scope) ? Visibility.Visible : Visibility.Collapsed;
            window.UninstallButton.Visibility = AppInstall.ShowUninstall(scope) ? Visibility.Visible : Visibility.Collapsed;
            window.UninstallButton.Content = UiText.UninstallLabel();
            uninstallArmed = false;
        }

        private static InstallScope CurrentScope()
        {
            var path = Environment.ProcessPath ?? "";
            return AppInstall.ScopeOf(path, AppInstall.PerUserDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), AppInstall.MachineDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
        }

        public void InstallForUser(object sender, RoutedEventArgs e)
        {
            if (installBusy) return;
            installBusy = true;
            try
            {
                var source = Environment.ProcessPath ?? "";
                if (!AppInstall.CanInstall(source))
                {
                    window.HostNotice(UiText.InstallNeedsPublished());
                    return;
                }
                var destination = AppInstaller.InstallPerUser(new AppInstaller.PerUserRequest(source, AppInstall.PerUserDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), Environment.GetFolderPath(Environment.SpecialFolder.Programs), window.InstallAutostart.IsChecked == true));
                AppInstaller.Relaunch(destination);
            }
            catch (Exception ex) { window.HostNotice(UiText.InstallFailed(ex), problem: true); }
            finally { installBusy = false; }
        }

        public async void InstallForMachine(object sender, RoutedEventArgs e)
        {
            if (installBusy) return;
            var source = Environment.ProcessPath ?? "";
            if (!AppInstall.CanInstall(source))
            {
                window.HostNotice(UiText.InstallNeedsPublished());
                return;
            }
            installBusy = true;
            try
            {
                Process? elevated;
                try
                {
                    elevated = Process.Start(new ProcessStartInfo(source, AppCommand.InstallMachine) { UseShellExecute = true, Verb = "runas" });
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    window.HostNotice(UiText.InstallCancelled());
                    return;
                }
                if (elevated == null) throw new InvalidOperationException("설치 권한 창을 열지 못했습니다.");
                await Task.Run(() => elevated.WaitForExit());
                if (elevated.ExitCode != 0) throw new InvalidOperationException("모든 사용자 설치가 끝나지 않았습니다.");
                AppInstaller.Relaunch(AppInstall.Executable(AppInstall.MachineDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))));
            }
            catch (Exception ex) { window.HostNotice(UiText.InstallFailed(ex), problem: true); }
            finally { installBusy = false; }
        }

        public async void UninstallApp(object sender, RoutedEventArgs e)
        {
            if (installBusy) return;
            if (!uninstallArmed)
            {
                uninstallArmed = true;
                window.UninstallButton.Content = UiText.UninstallConfirm();
                return;
            }
            installBusy = true;
            try
            {
                if (CurrentScope() == InstallScope.Machine)
                {
                    var source = Environment.ProcessPath ?? "";
                    Process? elevated;
                    try { elevated = Process.Start(new ProcessStartInfo(source, AppCommand.UninstallMachine) { UseShellExecute = true, Verb = "runas" }); }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                    {
                        uninstallArmed = false;
                        window.UninstallButton.Content = UiText.UninstallLabel();
                        window.HostNotice(UiText.RemoveCancelled());
                        return;
                    }
                    if (elevated == null) throw new InvalidOperationException("제거 권한 창을 열지 못했습니다.");
                    await Task.Run(() => elevated.WaitForExit());
                    if (elevated.ExitCode != 0) throw new InvalidOperationException("설치 제거가 끝나지 않았습니다.");
                    AppLifetime.RequestExit(force: true);
                    return;
                }
                AppInstaller.ClearUserAutostart();
                var shortcut = AppInstall.ShortcutPath(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
                if (File.Exists(shortcut)) File.Delete(shortcut);
                AppInstaller.DeleteAfterExit(AppInstall.PerUserDirectory(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
                AppLifetime.RequestExit(force: true);
            }
            catch (Exception ex)
            {
                window.HostNotice(UiText.RemoveFailed(ex), problem: true);
                uninstallArmed = false;
                window.UninstallButton.Content = UiText.UninstallLabel();
            }
            finally { installBusy = false; }
        }
    }
}
