using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace RemoteDesktop;

public sealed partial class RemoteHost
{
    private void MapAuthentication(WebApplication routes)
    {
        routes.MapPost("/v1/login", async (HttpContext c) =>
        {
            if (!await loginGate.WaitAsync(0)) return Results.StatusCode(429);
            try
            {
                if (DateTime.UtcNow < nextLogin) return Results.StatusCode(429);
                nextLogin = DateTime.UtcNow.AddSeconds(2);
                var login = await ReadSmall<Login>(c);
                if (login?.Password is null || !settings.Verify(login.Password)) return Results.Unauthorized();
                lock (gate)
                {
                    if (token != null) return Results.Conflict(new { error = "사용 중인 서버입니다." });
                    session.Dispose(); session = new();
                    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    lastSeen = DateTime.UtcNow;
                    var peer = UiText.PeerLabel(c.Connection.RemoteIpAddress);
                    sessionPeer = peer.Length == 0 ? null : peer;
                    Status?.Invoke(UiText.ClientConnected(c.Connection.RemoteIpAddress));
                    return Results.Ok(new { token, protocol = 1, maxFileBytes = MaxFileBytes });
                }
            }
            finally { loginGate.Release(); }
        });
        routes.MapPost("/v1/pake/start", (Delegate)(Func<HttpContext, Task<IResult>>)StartOpaqueLogin);
        routes.MapPost("/v1/pake/finish", (Delegate)(Func<HttpContext, Task<IResult>>)FinishOpaqueLogin);
        routes.MapPost("/v1/logout", (HttpContext c) => { lock (gate) { if (Authorized(c)) Disconnect(); } return Results.Ok(); });
        routes.MapGet("/v1/ping", () => Results.Ok(new { protocol = 1 }));
    }

    private void MapClipboard(WebApplication routes)
    {
        routes.MapGet("/v1/clipboard", async (HttpContext c) =>
        { var (scope, cancellation) = ClipboardContext(c); using (cancellation) return Results.Ok(await clipboard.Read(scope, cancellation.Token)); });
        routes.MapPost("/v1/clipboard/uploads", async (HttpContext c) =>
        {
            var request = await ReadSmall<ClipboardUpload>(c) ?? throw new ArgumentException();
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation) return Results.Ok(new { id = await clipboard.Begin(scope, request, cancellation.Token) });
        });
        routes.MapPut("/v1/clipboard/text", async (HttpContext c) =>
        {
            var request = await ReadSmall<ClipboardTextUpdate>(c, 6_100_000) ?? throw new ArgumentException();
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation) return Results.Ok(await clipboard.SetText(scope, request, cancellation.Token));
        });
        routes.MapPut("/v1/clipboard/uploads/{id}/{index:int}", async (string id, int index, HttpContext c) =>
        {
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation) await clipboard.Receive(scope, id, index, c.Request.Body, c.Request.Headers["X-Content-SHA256"].ToString(), cancellation.Token);
            return Results.Ok();
        });
        routes.MapPost("/v1/clipboard/uploads/{id}/commit", async (string id, HttpContext c) =>
        { var (scope, cancellation) = ClipboardContext(c); using (cancellation) return Results.Ok(await clipboard.Commit(scope, id, cancellation.Token)); });
        routes.MapDelete("/v1/clipboard/uploads/{id}", async (string id, HttpContext c) =>
        { var (scope, cancellation) = ClipboardContext(c); using (cancellation) await clipboard.Cancel(scope, id); return Results.Ok(); });
        routes.MapGet("/v1/clipboard/{id}/{index:int}", async (string id, int index, HttpContext c) =>
        {
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation)
            await using (var stream = await clipboard.Open(scope, id, index, cancellation.Token))
            {
                var hash = await SHA256.HashDataAsync(stream, cancellation.Token); stream.Position = 0;
                c.Response.ContentType = "application/octet-stream"; c.Response.ContentLength = stream.Length;
                c.Response.Headers["X-Content-SHA256"] = Convert.ToHexString(hash);
                await stream.CopyToAsync(c.Response.Body, cancellation.Token);
            }
        });
    }

    private void MapDesktop(WebApplication routes)
    {
        routes.MapGet("/v1/displays", () => Results.Ok(Desktop.Displays()));
        routes.MapGet("/v1/frame", async (HttpContext c) =>
        {
            await captureGate.WaitAsync(c.RequestAborted);
            var requestedQuality = int.TryParse(c.Request.Query["quality"].FirstOrDefault(), out var quality) ? quality : 55;
            var requestedRate = int.TryParse(c.Request.Query["fps"].FirstOrDefault(), out var fps) ? fps : 15;
            var localCursor = c.Request.Query["cursor"].FirstOrDefault() == "local";
            var viewport = ParseViewport(c.Request.Query["view"].FirstOrDefault());
            try
            {
                var frame = desktop.CaptureDetailed(c.Request.Query["display"].FirstOrDefault(), requestedQuality, viewport.X, viewport.Y, viewport.Width, viewport.Height, requestedRate, localCursor);
                c.Response.Headers["X-Capture-Ms"] = frame.Timing.CaptureMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Encode-Ms"] = frame.Timing.EncodeMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Frame-Reused"] = frame.Timing.Reused ? "1" : "0";
                c.Response.Headers["X-Jpeg-Quality"] = frame.Timing.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Cursor-X"] = frame.CursorX.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Cursor-Y"] = frame.CursorY.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Cursor-Local"] = frame.LocalCursor ? "1" : "0";
                if (!RemoteClient.SendJpeg(c.Request.Query["cached"].FirstOrDefault() == "1", frame.Timing.Reused))
                    return Results.Bytes(Array.Empty<byte>(), "image/jpeg");
                return Results.Bytes(frame.Bytes, "image/jpeg");
            }
            finally { captureGate.Release(); }
        });
        routes.MapPost("/v1/input", async (HttpContext c) =>
        {
            var input = await ReadSmall<InputEvent>(c) ?? throw new ArgumentException();
            lock (gate) { if (!Authorized(c)) return Results.Unauthorized(); desktop.Apply(input); }
            return Results.Ok();
        });
    }
}
