using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OPAQUE.Net;
using OPAQUE.Net.Types.Results;
using System.Collections.Concurrent;

namespace RemoteDesktop;

public readonly record struct InputRouteMetrics(long Requests, double AuthGateAverageMs, double ApplyAverageMs, double NoticeGateAverageMs);

public sealed partial class RemoteHost : IAsyncDisposable
{
    private sealed record AuthenticatedRequest(string Token, CancellationTokenSource SessionSource, CancellationTokenSource RequestSource);
    public const long MaxFileBytes = 1024L * 1024 * 1024;
    private readonly HostSettings settings;
    private readonly Desktop desktop;
    private readonly object gate = new();
    private readonly object videoRetirementGate = new();
    private readonly SemaphoreSlim loginGate = new(1);
    private readonly LoginThrottle loginThrottle = new();
    private readonly SemaphoreSlim captureGate = new(1);
    private readonly SemaphoreSlim opaqueGate = new(1);
    private readonly SourceIntervalThrottle opaqueStartThrottle = new();
    private readonly ConcurrentDictionary<string, PendingOpaqueLogin> pendingOpaqueLogins = new();
    private WebApplication? app;
    private Timer? timer;
    private int uploadExpiryBusy;
    private string? token;
    private string? sessionPeer;
    private DateTime? sessionStarted;
    private DateTime lastSeen;
    private string serverFingerprint = "";
    private CancellationTokenSource session = new();
    private int disposeState;
    private int listenPort;
    private readonly ClipboardExchange clipboard;
    private Task clipboardCleanup = Task.CompletedTask;
    private Task videoRetirement = Task.CompletedTask;
    private H264Session? video;
    private int disconnectInProgress;
    private long inputRequestCount;
    private long inputAuthGateTicks;
    private long inputApplyTicks;
    private long inputNoticeGateTicks;
    private long authorizationCheckCount;
    public event Action<string>? Status;
    public bool HasSession { get { lock (gate) return token != null; } }
    public string? SessionPeer { get { lock (gate) return sessionPeer; } }
    public DateTime? SessionStartedUtc { get { lock (gate) return sessionStarted; } }
    public DateTime? SessionLastSeenUtc { get { lock (gate) return token == null ? null : lastSeen; } }
    public long AuthorizationCheckCount => Interlocked.Read(ref authorizationCheckCount);
    public InputRouteMetrics InputMetrics
    {
        get
        {
            var requests = Interlocked.Read(ref inputRequestCount);
            var divisor = Math.Max(1, requests);
            return new InputRouteMetrics(requests,
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref inputAuthGateTicks)).TotalMilliseconds / divisor,
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref inputApplyTicks)).TotalMilliseconds / divisor,
                Stopwatch.GetElapsedTime(0, Interlocked.Read(ref inputNoticeGateTicks)).TotalMilliseconds / divisor);
        }
    }
    public Task<int> ExpireIdleUploads(TimeSpan idle) => clipboard.ExpireIdleUploads(idle);
    public RemoteHost(HostSettings settings, IFileClipboard? fileClipboard = null, string? clipboardCache = null)
    {
        this.settings = settings;
        Desktop.SmoothCapture = settings.UseSmoothHost;
        desktop = new();
        clipboard = new ClipboardExchange(fileClipboard ?? new WindowsFileClipboard(), clipboardCache ?? Path.Combine(HostSettings.DataDirectory, "ClipboardCache", "host"));
    }
    public async Task StartAsync(int port, X509Certificate2 certificate)
    {
        SecureDesktop.Configure(SecureDesktop.ShouldEnable(settings.SecureDesktopEnabled, SecureDesktop.IsMachineServiceRunning(), SecureDesktop.ReadPort() != 0));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = MaxFileBytes;
            k.ListenAnyIP(port, endpoint => endpoint.UseHttps(certificate));
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            CancellationTokenSource? sessionRequest = null;
            CancellationTokenRegistration abortOnRevocation = default;
            try
            {
                if (!IsPublicAuthenticationPath(context.Request.Path))
                {
                    var inputRequest = context.Request.Path == "/v1/input";
                    var authorized = false;
                    long gateTicks = 0;
                    lock (gate)
                    {
                        var gateEntered = Stopwatch.GetTimestamp();
                        if (Authorized(context))
                        {
                            lastSeen = DateTime.UtcNow;
                            var sessionSource = session;
                            sessionRequest = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, sessionSource.Token);
                            context.Items["auth"] = new AuthenticatedRequest(token!, sessionSource, sessionRequest);
                            authorized = true;
                        }
                        else context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        gateTicks = Stopwatch.GetTimestamp() - gateEntered;
                    }
                    if (inputRequest) Interlocked.Add(ref inputAuthGateTicks, gateTicks);
                    if (!authorized) return;
                    if (context.Request.Path != "/v1/logout") abortOnRevocation = sessionRequest!.Token.Register(context.Abort);
                }
                await next(context);
            }
            catch (OperationCanceledException) { if (!context.Response.HasStarted) context.Response.StatusCode = 408; }
            catch (ClipboardConflictException e) { await WriteClientError(context, 409, e.Message); }
            catch (ClipboardIncompleteException e)
            {
                if (!context.Response.HasStarted) context.Response.Headers["X-Received-Bytes"] = e.Received.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await WriteClientError(context, 409, e.Message);
            }
            catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException or BadHttpRequestException)
            { await WriteClientError(context, 400, e.Message); }
            catch (Exception) { if (!context.Response.HasStarted) context.Response.StatusCode = 500; }
            finally
            {
                abortOnRevocation.Dispose();
                sessionRequest?.Dispose();
            }
        });
        serverFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        MapAuthentication(app);
        MapClipboard(app);
        MapDesktop(app);
        if (settings.UseSmoothHost) HostPerformance.EnableHostPriority();
        await app.StartAsync();
        try { await clipboard.Sweep(CancellationToken.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
        timer = new Timer(_ =>
        {
            bool expired;
            lock (gate) expired = token != null && DateTime.UtcNow - lastSeen > TimeSpan.FromSeconds(15);
            if (expired) _ = Task.Run(() => Disconnect(UiText.SessionEndKind.TimedOut));
            if (Interlocked.Exchange(ref uploadExpiryBusy, 1) != 0) return;
            _ = clipboard.ExpireIdleUploads(ClipboardExchange.PendingUploadIdle).ContinueWith(task =>
            {
                Interlocked.Exchange(ref uploadExpiryBusy, 0);
                _ = task.Exception;
            }, TaskScheduler.Default);
        }, null, 1000, 1000);
        listenPort = port;
        Status?.Invoke(UiText.Listening(port));
    }
    private static bool IsPublicAuthenticationPath(PathString path) => path == "/v1/login" || path == "/v1/pake/start" || path == "/v1/pake/finish";
    private static async Task WriteClientError(HttpContext context, int status, string? message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        var text = message?.Trim() ?? "";
        if (text.Length == 0 || text.Length > 300 || text.IndexOfAny(new[] { '\n', '\r' }) >= 0) return;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(text);
    }
    private async Task<IResult> StartOpaqueLogin(HttpContext context)
    {
        if (!settings.HasOpaqueIdentity) return Results.Conflict(new { error = "Windows secure enrollment required" });
        var source = AuthenticationSource(context);
        if (!opaqueStartThrottle.TryAcquire(source, DateTime.UtcNow, TimeSpan.FromSeconds(2), out var retry))
        {
            context.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.StatusCode(429);
        }
        if (!await opaqueGate.WaitAsync(0)) return Results.StatusCode(429);
        try
        {
            foreach (var stale in pendingOpaqueLogins.Where(item => item.Value.ExpiresAt <= DateTime.UtcNow).ToArray()) pendingOpaqueLogins.TryRemove(stale.Key, out _);
            if (pendingOpaqueLogins.Count >= 32 || pendingOpaqueLogins.Values.Count(item => item.Source == source) >= 2) return Results.StatusCode(429);

            var request = await ReadSmall<OpaqueStartRequest>(context);
            if (request is null || !ValidNonce(request.Nonce) || string.IsNullOrEmpty(request.LoginRequest) || request.LoginRequest.Length > 16384) throw new ArgumentException("Invalid OPAQUE request");
            var identity = settings.OpaqueIdentityValue();
            var opaqueServer = new OpaqueServer();
            if (!opaqueServer.StartLogin(identity.ServerSetup, request.LoginRequest, HostSettings.OpaqueUserIdentifier, identity.RegistrationRecord,
                    HostSettings.OpaqueClientIdentifier, HostSettings.OpaqueServerIdentifier, out var result, out _)
                || result is null) return Results.Unauthorized();
            var exchangeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            if (!pendingOpaqueLogins.TryAdd(exchangeId, new(result, request.Nonce, DateTime.UtcNow.AddSeconds(30), source))) return Results.StatusCode(503);
            return Results.Ok(new { exchangeId, loginResponse = result.LoginResponse });
        }
        finally { opaqueGate.Release(); }
    }
    private async Task<IResult> FinishOpaqueLogin(HttpContext context)
    {
        var request = await ReadSmall<OpaqueFinishRequest>(context);
        if (request is null || !ValidNonce(request.Nonce) || string.IsNullOrEmpty(request.ExchangeId) || request.ExchangeId.Length > 100 || string.IsNullOrEmpty(request.FinishLoginRequest) || request.FinishLoginRequest.Length > 16384) throw new ArgumentException("Invalid OPAQUE finish request");
        if (!pendingOpaqueLogins.TryRemove(request.ExchangeId, out var pending) || pending.ExpiresAt <= DateTime.UtcNow || pending.Nonce != request.Nonce || pending.Source != AuthenticationSource(context)) return Results.Unauthorized();
        var opaqueServer = new OpaqueServer();
        if (!opaqueServer.FinishLogin(pending.State.ServerLoginState, request.FinishLoginRequest, out var sessionKey) || string.IsNullOrEmpty(sessionKey)) return Results.Unauthorized();
        var proof = PakeBootstrap.ComputeProof(sessionKey, request.Nonce, serverFingerprint);
        return Results.Ok(new { fingerprint = serverFingerprint, nonce = request.Nonce, proof });
    }
    private static bool ValidNonce(string nonce)
    {
        try { return Convert.FromBase64String(nonce).Length == 32 && nonce.Length <= 64; }
        catch (FormatException) { return false; }
    }
    private static string AuthenticationSource(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return address.ToString();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var prefix = address.GetAddressBytes();
            Array.Clear(prefix, 8, 8);
            var scope = address.IsIPv6LinkLocal ? address.ScopeId : 0;
            return new System.Net.IPAddress(prefix, scope).ToString();
        }
        return address.ToString();
    }
    private static async Task<T?> ReadSmall<T>(HttpContext c, int maxBytes = 32768)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var data = new MemoryStream();
        var buffer = new byte[4096];
        int n;
        while ((n = await c.Request.Body.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (data.Length + n > maxBytes) throw new ArgumentException("Request too large");
            data.Write(buffer, 0, n);
        }
        return System.Text.Json.JsonSerializer.Deserialize<T>(data.ToArray(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }
    private bool Authorized(HttpContext c)
    {
        Interlocked.Increment(ref authorizationCheckCount);
        return BearerTokenMatches(c.Request.Headers.Authorization.ToString(), token);
    }
    private static bool BearerTokenMatches(string? header, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken) || !AuthenticationHeaderValue.TryParse(header, out var parsed)
            || !string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(parsed.Parameter)
            || parsed.Parameter.Length != expectedToken.Length) return false;
        var actual = Encoding.UTF8.GetBytes(parsed.Parameter);
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
    private static AuthenticatedRequest? Authenticated(HttpContext context)
        => context.Items.TryGetValue("auth", out var value) ? value as AuthenticatedRequest : null;
    private static CancellationToken SessionRequestToken(HttpContext context)
        => Authenticated(context)?.RequestSource.Token ?? context.RequestAborted;
    private static (double X, double Y, double Width, double Height) ParseViewport(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0, 0, 1, 1);
        var parts = value.Split(',');
        if (parts.Length != 4 || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x) || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y) || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var width) || !double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var height)) throw new ArgumentException("Invalid viewport");
        return (x, y, width, height);
    }
    private (string Scope, CancellationTokenSource Cancellation) ClipboardContext(HttpContext c)
    {
        var authenticated = Authenticated(c) ?? throw new OperationCanceledException();
        return (authenticated.Token, CancellationTokenSource.CreateLinkedTokenSource(authenticated.RequestSource.Token));
    }
    public void Disconnect(UiText.SessionEndKind reason = UiText.SessionEndKind.Left)
        => Disconnect(reason, expectedSession: null);

    private void Disconnect(UiText.SessionEndKind reason, CancellationTokenSource? expectedSession)
    {
        if (Interlocked.Exchange(ref disconnectInProgress, 1) != 0) return;
        string? oldToken;
        string? peer;
        bool hadSession;
        bool announce;
        try
        {
            lock (gate)
            {
                if (disposeState == 2 || (expectedSession != null && !ReferenceEquals(session, expectedSession))) return;
                peer = sessionPeer;
                hadSession = token != null;
                oldToken = token;
                token = null;
                sessionPeer = null;
                sessionStarted = null;
                try { session.Cancel(); }
                catch (AggregateException error) { Trace.TraceWarning("Session cancellation callback failed: {0}", error.GetType().Name); }
                announce = disposeState == 0;
            }
        }
        finally { Interlocked.Exchange(ref disconnectInProgress, 0); }

        if (oldToken != null)
        {
            Task ended;
            try { ended = clipboard.EndSession(oldToken); }
            catch (Exception error)
            {
                Trace.TraceWarning("Clipboard session cleanup failed: {0}", error.GetType().Name);
                ended = Task.CompletedTask;
            }
            lock (gate) clipboardCleanup = Task.WhenAll(clipboardCleanup, ended);
        }
        try { desktop.Release(); }
        catch (Exception error) { Trace.TraceWarning("Input release failed: {0}", error.GetType().Name); }
        _ = QueueRetireVideo();
        if (announce)
        {
            try { Status?.Invoke(hadSession ? UiText.SessionEnded(peer, reason, listenPort) : UiText.Listening(listenPort)); }
            catch (Exception error) { Trace.TraceWarning("Session status update failed: {0}", error.GetType().Name); }
        }
    }

    private Task QueueRetireVideo()
    {
        lock (videoRetirementGate)
        {
            if (videoRetirement.IsCompleted) videoRetirement = Task.Run(RetireVideoUntilAvailable);
            return videoRetirement;
        }
    }

    private async Task RetireVideoUntilAvailable()
    {
        while (!await captureGate.WaitAsync(TimeSpan.FromSeconds(1))) await Task.Delay(250);
        try
        {
            var retired = video;
            video = null;
            try { retired?.Dispose(); }
            catch (Exception error) { Trace.TraceWarning("Video session cleanup failed: {0}", error.GetType().Name); }
        }
        finally { captureGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref disposeState, 1, 0) != 0) return;
        try
        {
            var currentTimer = timer;
            timer = null;
            if (currentTimer != null) await currentTimer.DisposeAsync();
            SecureDesktop.Configure(false);
            Disconnect();
            var runningApp = app;
            app = null;
            if (runningApp != null)
            {
                try { await runningApp.StopAsync(); }
                finally { await runningApp.DisposeAsync(); }
            }
            await QueueRetireVideo();
            await clipboardCleanup;
        }
        finally
        {
            HostPerformance.RestoreProcessPriority();
            lock (gate) { session.Dispose(); disposeState = 2; }
        }
    }
    private record Login(string Password);
    private sealed record OpaqueStartRequest(string LoginRequest, string Nonce);
    private sealed record OpaqueFinishRequest(string ExchangeId, string FinishLoginRequest, string Nonce);
    private sealed record PendingOpaqueLogin(StartServerLoginResult State, string Nonce, DateTime ExpiresAt, string Source);
}
