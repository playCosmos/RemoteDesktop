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
                    if (token != null && !UiText.ReplaceQuietSession(DateTime.UtcNow - lastSeen)) return Results.Conflict(new { error = "사용 중인 서버입니다." });
                    if (token != null)
                    {
                        var oldToken = token;
                        token = null;
                        sessionPeer = null;
                        sessionStarted = null;
                        session.Cancel();
                        if (oldToken != null) clipboardCleanup = clipboard.EndSession(oldToken);
                        try { desktop.Release(); } catch (InvalidOperationException) { }
                    }
                    session.Dispose(); session = new();
                    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    lastSeen = DateTime.UtcNow;
                    var peer = UiText.PeerLabel(c.Connection.RemoteIpAddress);
                    sessionPeer = peer.Length == 0 ? null : peer;
                    sessionStarted = DateTime.UtcNow;
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
            var request = await ReadSmall<ClipboardTextUpdate>(c, 12_500_000) ?? throw new ArgumentException();
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation) return Results.Ok(await clipboard.SetText(scope, request, cancellation.Token));
        });
        routes.MapPut("/v1/clipboard/image", async (HttpContext c) =>
        {
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation) return Results.Ok(await clipboard.SetImage(scope, c.Request.Body, c.Request.Headers["X-Clipboard-Origin"].ToString(), c.Request.Headers["X-Clipboard-Revision"].ToString(), c.Request.Headers["X-Content-SHA256"].ToString(), cancellation.Token));
        });
        routes.MapGet("/v1/clipboard/{id}/image", async (string id, HttpContext c) =>
        {
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation)
            {
                var bytes = await clipboard.OpenImage(scope, id, cancellation.Token);
                c.Response.ContentType = "image/png"; c.Response.ContentLength = bytes.Length;
                c.Response.Headers["X-Content-SHA256"] = Convert.ToHexString(SHA256.HashData(bytes));
                await c.Response.Body.WriteAsync(bytes, cancellation.Token);
            }
        });
        routes.MapGet("/v1/clipboard/{id}/html", async (string id, HttpContext c) =>
        {
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation)
            {
                var html = await clipboard.OpenHtml(scope, id, cancellation.Token);
                var bytes = System.Text.Encoding.UTF8.GetBytes(html);
                c.Response.ContentType = "text/html; charset=utf-8";
                c.Response.ContentLength = bytes.Length;
                c.Response.Headers["X-Content-SHA256"] = Convert.ToHexString(SHA256.HashData(bytes));
                await c.Response.Body.WriteAsync(bytes, cancellation.Token);
            }
        });
        routes.MapPut("/v1/clipboard/uploads/{id}/{index:int}", async (string id, int index, HttpContext c) =>
        {
            var (scope, cancellation) = ClipboardContext(c);
            var offsetHeader = c.Request.Headers["X-Upload-Offset"].ToString();
            if (!string.IsNullOrEmpty(offsetHeader) && !long.TryParse(offsetHeader, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new ArgumentException("Invalid clipboard offset");
            var offset = string.IsNullOrEmpty(offsetHeader) ? 0 : long.Parse(offsetHeader, System.Globalization.CultureInfo.InvariantCulture);
            using (cancellation) await clipboard.Receive(scope, id, index, c.Request.Body, c.Request.Headers["X-Content-SHA256"].ToString(), cancellation.Token, offset);
            return Results.Ok();
        });
        routes.MapGet("/v1/clipboard/uploads/{id}/{index:int}", async (string id, int index, HttpContext c) =>
        {
            var (scope, cancellation) = ClipboardContext(c);
            using (cancellation) return Results.Ok(new { received = await clipboard.Received(scope, id, index, cancellation.Token) });
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
                long start = 0;
                if (TryResumeRange(c.Request.Headers.Range.ToString(), stream.Length, out var requested))
                {
                    if (requested < 0 || requested >= stream.Length) { c.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable; return; }
                    if (requested > 0)
                    {
                        start = requested; stream.Position = start; c.Response.StatusCode = StatusCodes.Status206PartialContent;
                        c.Response.Headers.ContentRange = "bytes " + start.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + (stream.Length - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + stream.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                c.Response.ContentType = "application/octet-stream"; c.Response.ContentLength = stream.Length - start;
                c.Response.Headers["X-Content-SHA256"] = Convert.ToHexString(hash);
                await stream.CopyToAsync(c.Response.Body, cancellation.Token);
            }
        });
    }

    private static bool TryResumeRange(string header, long length, out long start)
    {
        start = 0;
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var body = header["bytes=".Length..].Trim();
        var dash = body.IndexOf('-');
        if (dash <= 0 || !long.TryParse(body[..dash], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out start)) return false;
        var end = body[(dash + 1)..].Trim();
        if (end.Length == 0) return true;
        return long.TryParse(end, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var endByte) && length > 0 && endByte == length - 1;
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
                var held = System.Diagnostics.Stopwatch.StartNew();
                var frame = desktop.CaptureDetailed(c.Request.Query["display"].FirstOrDefault(), requestedQuality, viewport.X, viewport.Y, viewport.Width, viewport.Height, requestedRate, localCursor);
                held.Stop();
                c.Response.Headers["X-Capture-Ms"] = frame.Timing.CaptureMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Encode-Ms"] = frame.Timing.EncodeMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                var wait = Math.Max(0, held.Elapsed.TotalMilliseconds - frame.Timing.CaptureMilliseconds - frame.Timing.EncodeMilliseconds);
                c.Response.Headers["X-Wait-Ms"] = wait.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Frame-Reused"] = frame.Timing.Reused ? "1" : "0";
                c.Response.Headers["X-Jpeg-Quality"] = frame.Timing.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Secure-Desktop"] = SecureDesktop.Header(SecureDesktop.LastView);
                c.Response.Headers["X-Cursor-X"] = frame.CursorX.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Cursor-Y"] = frame.CursorY.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Cursor-Local"] = frame.LocalCursor ? "1" : "0";
                c.Response.Headers["X-Cursor-Visible"] = frame.CursorVisible ? "1" : "0";
                c.Response.Headers["X-Cursor-Hot-X"] = frame.CursorHotX.ToString(System.Globalization.CultureInfo.InvariantCulture);
                c.Response.Headers["X-Cursor-Hot-Y"] = frame.CursorHotY.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(frame.CursorHash)) c.Response.Headers["X-Cursor-Hash"] = frame.CursorHash;
                var knownShape = c.Request.Query["shape"].FirstOrDefault();
                if (frame.CursorVisible && frame.CursorPng is { Length: > 0 } png && !string.Equals(knownShape, frame.CursorHash, StringComparison.OrdinalIgnoreCase))
                {
                    var encoded = Convert.ToBase64String(png);
                    if (encoded.Length <= 24000) c.Response.Headers["X-Cursor-Png"] = encoded;
                }
                var skipped = !RemoteClient.SendJpeg(c.Request.Query["cached"].FirstOrDefault() == "1", frame.Timing.Reused);
                if (VideoCodec.Normalize(c.Request.Query["codec"].FirstOrDefault()) == "h264")
                {
                    video ??= new H264Session();
                    if (!video.Retired)
                    {
                        if (skipped)
                        {
                            video.NoteSkipped();
                            c.Response.Headers["X-Codec"] = "h264";
                            return Results.Bytes(Array.Empty<byte>(), "video/h264");
                        }
                        var picture = video.Encode(frame.Bytes, c.Request.Query["key"].FirstOrDefault() == "1", frame.Timing.Reused, requestedRate, frame.Timing.Quality);
                        if (picture.Length > 0)
                        {
                            c.Response.Headers["X-Codec"] = "h264";
                            return Results.Bytes(picture, "video/h264");
                        }
                    }
                }
                c.Response.Headers["X-Codec"] = "jpeg";
                if (skipped)
                    return Results.Bytes(Array.Empty<byte>(), "image/jpeg");
                return Results.Bytes(frame.Bytes, "image/jpeg");
            }
            finally { captureGate.Release(); }
        });
        routes.MapPost("/v1/input", async (HttpContext c) =>
        {
            var input = await ReadSmall<InputEvent>(c) ?? throw new ArgumentException();
            string? notice;
            lock (gate)
            {
                if (!Authorized(c)) return Results.Unauthorized();
                notice = desktop.Apply(input);
            }
            return string.IsNullOrEmpty(notice) ? Results.Ok() : Results.Ok(new { notice });
        });
    }
}
