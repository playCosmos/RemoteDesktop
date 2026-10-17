using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OPAQUE.Net;
using OPAQUE.Net.Types.Results;
using System.Collections.Concurrent;

namespace RemoteDesktop;

public sealed partial class RemoteHost : IAsyncDisposable
{
    public const long MaxFileBytes = 1024L * 1024 * 1024;
    private readonly HostSettings settings;
    private readonly Desktop desktop;
    private readonly object gate = new();
    private readonly SemaphoreSlim loginGate = new(1);
    private readonly SemaphoreSlim captureGate = new(1);
    private readonly SemaphoreSlim opaqueGate = new(1);
    private readonly ConcurrentDictionary<string, PendingOpaqueLogin> pendingOpaqueLogins = new();
    private WebApplication? app;
    private Timer? timer;
    private string? token;
    private string? sessionPeer;
    private DateTime? sessionStarted;
    private DateTime lastSeen;
    private DateTime nextLogin;
    private DateTime nextOpaqueLogin;
    private string serverFingerprint = "";
    private CancellationTokenSource session = new();
    private int disposeState;
    private int listenPort;
    private readonly ClipboardExchange clipboard;
    private Task clipboardCleanup = Task.CompletedTask;
    public event Action<string>? Status;
    public bool HasSession { get { lock (gate) return token != null; } }
    public string? SessionPeer { get { lock (gate) return sessionPeer; } }
    public DateTime? SessionStartedUtc { get { lock (gate) return sessionStarted; } }
    public DateTime? SessionLastSeenUtc { get { lock (gate) return token == null ? null : lastSeen; } }
    public RemoteHost(HostSettings settings, IFileClipboard? fileClipboard = null, string? clipboardCache = null)
    {
        this.settings = settings;
        Desktop.SmoothCapture = settings.UseSmoothHost;
        desktop = new();
        clipboard = new ClipboardExchange(fileClipboard ?? new WindowsFileClipboard(), clipboardCache ?? Path.Combine(HostSettings.DataDirectory, "ClipboardCache", "host"));
    }
    public async Task StartAsync(int port, X509Certificate2 certificate)
    {
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
            try
            {
                if (!IsPublicAuthenticationPath(context.Request.Path))
                {
                    lock (gate)
                    {
                        if (!Authorized(context)) { context.Response.StatusCode = 401; return; }
                        lastSeen = DateTime.UtcNow;
                    }
                }
                await next(context);
            }
            catch (OperationCanceledException) { if (!context.Response.HasStarted) context.Response.StatusCode = 408; }
            catch (ClipboardConflictException e) { await WriteClientError(context, 409, e.Message); }
            catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException or BadHttpRequestException)
            { await WriteClientError(context, 400, e.Message); }
            catch (Exception) { if (!context.Response.HasStarted) context.Response.StatusCode = 500; }
        });
        serverFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        MapAuthentication(app);
        MapClipboard(app);
        MapDesktop(app);
        if (settings.UseSmoothHost) HostPerformance.EnableHostPriority();
        await app.StartAsync();
        try { await clipboard.Sweep(CancellationToken.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException) { }
        timer = new Timer(_ => { lock (gate) { if (token != null && DateTime.UtcNow - lastSeen > TimeSpan.FromSeconds(15)) Disconnect(UiText.SessionEndKind.TimedOut); } }, null, 1000, 1000);
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
        if (!await opaqueGate.WaitAsync(0)) return Results.StatusCode(429);
        try
        {
            if (DateTime.UtcNow < nextOpaqueLogin) return Results.StatusCode(429);
            nextOpaqueLogin = DateTime.UtcNow.AddSeconds(2);
            foreach (var stale in pendingOpaqueLogins.Where(item => item.Value.ExpiresAt <= DateTime.UtcNow).ToArray()) pendingOpaqueLogins.TryRemove(stale.Key, out _);
            if (pendingOpaqueLogins.Count >= 8) return Results.StatusCode(429);

            var request = await ReadSmall<OpaqueStartRequest>(context);
            if (request is null || !ValidNonce(request.Nonce) || string.IsNullOrEmpty(request.LoginRequest) || request.LoginRequest.Length > 16384) throw new ArgumentException("Invalid OPAQUE request");
            var identity = settings.OpaqueIdentityValue();
            var opaqueServer = new OpaqueServer();
            if (!opaqueServer.StartLogin(identity.ServerSetup, request.LoginRequest, HostSettings.OpaqueUserIdentifier, identity.RegistrationRecord,
                    HostSettings.OpaqueClientIdentifier, HostSettings.OpaqueServerIdentifier, out var result, out _)
                || result is null) return Results.Unauthorized();
            var exchangeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            if (!pendingOpaqueLogins.TryAdd(exchangeId, new(result, request.Nonce, DateTime.UtcNow.AddSeconds(30)))) return Results.StatusCode(503);
            return Results.Ok(new { exchangeId, loginResponse = result.LoginResponse });
        }
        finally { opaqueGate.Release(); }
    }
    private async Task<IResult> FinishOpaqueLogin(HttpContext context)
    {
        var request = await ReadSmall<OpaqueFinishRequest>(context);
        if (request is null || !ValidNonce(request.Nonce) || string.IsNullOrEmpty(request.ExchangeId) || request.ExchangeId.Length > 100 || string.IsNullOrEmpty(request.FinishLoginRequest) || request.FinishLoginRequest.Length > 16384) throw new ArgumentException("Invalid OPAQUE finish request");
        if (!pendingOpaqueLogins.TryRemove(request.ExchangeId, out var pending) || pending.ExpiresAt <= DateTime.UtcNow || pending.Nonce != request.Nonce) return Results.Unauthorized();
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
    private bool Authorized(HttpContext c) => token != null && c.Request.Headers.Authorization == "Bearer " + token;
    private static (double X, double Y, double Width, double Height) ParseViewport(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (0, 0, 1, 1);
        var parts = value.Split(',');
        if (parts.Length != 4 || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x) || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y) || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var width) || !double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out var height)) throw new ArgumentException("Invalid viewport");
        return (x, y, width, height);
    }
    private (string Scope, CancellationTokenSource Cancellation) ClipboardContext(HttpContext c)
    {
        lock (gate)
        {
            if (!Authorized(c)) throw new OperationCanceledException();
            return (token!, CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, session.Token));
        }
    }
    public void Disconnect(UiText.SessionEndKind reason = UiText.SessionEndKind.Left)
    {
        lock (gate)
        {
            if (disposeState == 2) return;
            var peer = sessionPeer;
            var hadSession = token != null;
            var oldToken = token; token = null; sessionPeer = null; sessionStarted = null; session.Cancel();
            if (oldToken != null) clipboardCleanup = clipboard.EndSession(oldToken);
            try { desktop.Release(); } catch (InvalidOperationException) { }
            if (disposeState != 0) return;
            Status?.Invoke(hadSession ? UiText.SessionEnded(peer, reason, listenPort) : UiText.Listening(listenPort));
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref disposeState, 1, 0) != 0) return;
        try
        {
            timer?.Dispose();
            Disconnect();
            var runningApp = app;
            app = null;
            if (runningApp != null)
            {
                try { await runningApp.StopAsync(); }
                finally { await runningApp.DisposeAsync(); }
            }
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
    private sealed record PendingOpaqueLogin(StartServerLoginResult State, string Nonce, DateTime ExpiresAt);
}
