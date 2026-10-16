using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OPAQUE.Net;
using OPAQUE.Net.Types.Parameters;

namespace RemoteDesktop;

/// <summary>Uses OPAQUE only to securely learn and pin a Windows host's TLS certificate.</summary>
public static class PakeBootstrap
{
    private const string ProofDomain = "PersonalRemoteDesktop/TLS-Pin-Bootstrap/v1";

    public static async Task<string> AcquireServerFingerprint(string address, string password, CancellationToken cancellationToken = default)
    {
        if (password.Length is < 1 or > 1024) throw new ArgumentException("접속 비밀번호를 입력하세요.");
        var endpoint = BuildBaseUri(address);
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            // This client is used only for the OPAQUE key-bootstrap exchange, never for control APIs.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null
        };
        using var http = new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var opaqueClient = new OpaqueClient();
        if (!opaqueClient.StartLogin(password, out var started) || started is null)
            throw new AuthenticationException("보안 접속 교환을 시작하지 못했습니다.");
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var startResponse = await http.PostAsJsonAsync("pake/start", new { loginRequest = started.StartLoginRequest, nonce }, timeout.Token);
        if (startResponse.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("서버에 비밀번호 기반 키 등록이 없습니다. 호스트를 업데이트하고 서버에서 현재 비밀번호를 한 번 다시 등록하세요.");
        if (startResponse.StatusCode == HttpStatusCode.TooManyRequests)
            throw new InvalidOperationException("접속 시도가 제한되었습니다. 잠시 후 다시 시도하세요.");
        if (startResponse.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthenticationException("비밀번호가 일치하지 않거나 서버 인증에 실패했습니다.");
        startResponse.EnsureSuccessStatusCode();
        var challenge = await startResponse.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        var exchangeId = challenge.GetProperty("exchangeId").GetString() ?? throw new AuthenticationException("잘못된 보안 접속 응답입니다.");
        var loginResponse = challenge.GetProperty("loginResponse").GetString() ?? throw new AuthenticationException("잘못된 보안 접속 응답입니다.");
        var config = KSFConfig.Create(KSFConfigType.MemoryConstrained, null, null, null);
        if (!opaqueClient.FinishLogin(started.ClientLoginState, loginResponse, password, HostSettings.OpaqueClientIdentifier, HostSettings.OpaqueServerIdentifier, config, out var finished) || finished is null)
            throw new AuthenticationException("비밀번호가 틀렸거나 서버 인증에 실패했습니다.");

        using var finishResponse = await http.PostAsJsonAsync("pake/finish", new
        {
            exchangeId,
            finishLoginRequest = finished.FinishLoginRequest,
            nonce
        }, timeout.Token);
        if (finishResponse.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthenticationException("비밀번호가 일치하지 않거나 서버 인증에 실패했습니다.");
        finishResponse.EnsureSuccessStatusCode();
        var receipt = await finishResponse.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        var fingerprint = receipt.GetProperty("fingerprint").GetString() ?? throw new AuthenticationException("서버 지문을 받지 못했습니다.");
        var proof = receipt.GetProperty("proof").GetString() ?? throw new AuthenticationException("서버 키 증명을 받지 못했습니다.");
        if (receipt.GetProperty("nonce").GetString() != nonce || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new AuthenticationException("서버 키 응답이 올바르지 않습니다.");
        byte[] received, expected;
        try { received = Convert.FromHexString(proof); expected = Convert.FromHexString(ComputeProof(finished.SessionKey, nonce, fingerprint)); }
        catch (FormatException) { throw new AuthenticationException("서버 키 증명이 올바르지 않습니다."); }
        if (!CryptographicOperations.FixedTimeEquals(received, expected)) throw new AuthenticationException("서버 키 증명에 실패했습니다.");
        return fingerprint.ToUpperInvariant();
    }

    internal static string ComputeProof(string sessionKey, string nonce, string fingerprint)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(sessionKey));
        try
        {
            using var hmac = new HMACSHA256(key);
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ProofDomain}\n{nonce}\n{fingerprint}")));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static Uri BuildBaseUri(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("서버 주소를 입력하세요.");
        var uri = new Uri("https://" + address.Trim().TrimEnd('/') + "/v1/");
        if (uri.AbsolutePath != "/v1/" || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("주소에는 호스트와 포트만 입력하세요.");
        return uri;
    }
}
