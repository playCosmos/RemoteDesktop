using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using OPAQUE.Net;
using OPAQUE.Net.Types.Parameters;

namespace RemoteDesktop;

public sealed class HostSettings
{
    public string Salt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string OpaqueIdentityProtected { get; set; } = "";
    public int Port { get; set; } = 45831;
    public bool? SmoothHost { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool UseSmoothHost => SmoothHost != false;
    [System.Text.Json.Serialization.JsonIgnore] public bool HasPassword => Salt.Length > 0 && PasswordHash.Length > 0;
    [System.Text.Json.Serialization.JsonIgnore] public bool HasOpaqueIdentity => OpaqueIdentityProtected.Length > 0;
    public const int Iterations = 600_000;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalRemoteDesktop");
    [System.Text.Json.Serialization.JsonIgnore] private string? SettingsPath { get; set; }
    public static HostSettings Load(string? path = null)
    {
        path = ResolveSettingsPath(path);
        var settings = File.Exists(path) ? JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(path))! : new();
        settings.SettingsPath = path;
        return settings;
    }
    public void SetPassword(string password, string? path = null)
    {
        if (password.Length < 8 || password.Length > 1024) throw new ArgumentException("비밀번호를 8~1024자 입력하세요.");
        var salt = RandomNumberGenerator.GetBytes(32);
        var passwordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32));
        var opaqueIdentity = CreateOpaqueIdentity(password);
        Salt = Convert.ToBase64String(salt);
        PasswordHash = passwordHash;
        OpaqueIdentityProtected = ProtectOpaqueIdentity(opaqueIdentity);
        Save(path);
    }
    public void EnrollOpaqueIdentity(string currentPassword, string? path = null)
    {
        if (!HasPassword || !Verify(currentPassword)) throw new CryptographicException("기존 서버 비밀번호가 일치하지 않습니다.");
        OpaqueIdentityProtected = ProtectOpaqueIdentity(CreateOpaqueIdentity(currentPassword));
        Save(path);
    }
    public OpaqueIdentity OpaqueIdentityValue()
    {
        if (!HasOpaqueIdentity) throw new InvalidOperationException("Windows password-key enrollment is required.");
        var encrypted = Convert.FromBase64String(OpaqueIdentityProtected);
        byte[] bytes;
        try { bytes = ProtectedData.Unprotect(encrypted, OpaqueEntropy, DataProtectionScope.LocalMachine); }
        catch (CryptographicException)
        {
            bytes = ProtectedData.Unprotect(encrypted, OpaqueEntropy, DataProtectionScope.CurrentUser);
            OpaqueIdentityProtected = ProtectOpaqueIdentity(JsonSerializer.Deserialize<OpaqueIdentity>(bytes) ?? throw new CryptographicException("Invalid OPAQUE identity data."));
            Save();
        }
        return JsonSerializer.Deserialize<OpaqueIdentity>(bytes) ?? throw new CryptographicException("Invalid OPAQUE identity data.");
    }
    public void Save(string? path = null)
    {
        path = ResolveSettingsPath(path ?? SettingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, true);
            SettingsPath = path;
        }
        catch (UnauthorizedAccessException e)
        { throw new UnauthorizedAccessException($"서버 설정 파일에 접근할 수 없습니다: {path}", e); }
        catch (IOException e)
        { throw new IOException($"서버 설정 파일을 저장하지 못했습니다: {path}", e); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
    internal static string ResolveSettingsPath(string? pathOrDirectory)
    {
        if (string.IsNullOrWhiteSpace(pathOrDirectory))
            pathOrDirectory = Path.Combine(DataDirectory, "settings.json");
        var path = Path.GetFullPath(pathOrDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (Directory.Exists(path)) return Path.Combine(path, "settings.json");
        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return path;
        if (!File.Exists(path)) return Path.Combine(path, "settings.json");
        var holding = path + ".settings-migrating";
        File.Move(path, holding, true);
        Directory.CreateDirectory(path);
        var destination = Path.Combine(path, "settings.json");
        if (File.Exists(destination)) File.Delete(holding);
        else File.Move(holding, destination);
        return destination;
    }
    public bool Verify(string password) => PasswordHash.Length > 0 && password.Length <= 1024 &&
        CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(PasswordHash), Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(Salt), Iterations, HashAlgorithmName.SHA256, 32));

    public static X509Certificate2 Certificate(string? dataDirectory = null, bool createIfMissing = true)
    {
        dataDirectory ??= DataDirectory;
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "identity.bin");
        if (File.Exists(path))
        {
            var encrypted = File.ReadAllBytes(path);
            byte[] pfx;
            try { pfx = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine); }
            catch (CryptographicException)
            {
                pfx = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(path, ProtectedData.Protect(pfx, null, DataProtectionScope.LocalMachine));
            }
            try { return LoadIdentity(pfx); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
        if (!createIfMissing) throw new FileNotFoundException("서버 인증서가 없습니다. 서버 UI를 한 번 실행해 초기화하세요.", path);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=PersonalRemoteDesktop", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var newPfx = certificate.Export(X509ContentType.Pfx);
        File.WriteAllBytes(path, ProtectedData.Protect(newPfx, null, DataProtectionScope.LocalMachine));
        return LoadIdentity(newPfx);
    }
    internal static X509Certificate2 LoadIdentity(byte[] pfx)
    {
        // Windows Schannel needs a file-backed private key for server TLS. EphemeralKeySet
        // keeps the key only in memory and can make Kestrel handshakes fail with
        // SEC_E_NO_CREDENTIALS. UserKeySet keeps the temporary key in this user's profile,
        // without requiring elevation or persisting it after this certificate is disposed.
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        if (certificate.HasPrivateKey) return certificate;
        certificate.Dispose();
        throw new CryptographicException("서버 인증서에서 TLS 개인 키를 불러오지 못했습니다.");
    }

    private static OpaqueIdentity CreateOpaqueIdentity(string password)
    {
        var server = new OpaqueServer();
        var client = new OpaqueClient();
        if (!server.CreateSetup(out var setup) || string.IsNullOrEmpty(setup)) throw new CryptographicException("Failed to create OPAQUE server setup.");
        if (!client.StartRegistration(password, out var started) || started is null) throw new CryptographicException("Failed to start OPAQUE registration.");
        if (!server.CreateRegistrationResponse(setup, OpaqueUserIdentifier, started.RegistrationRequest, out var response) || string.IsNullOrEmpty(response)) throw new CryptographicException("Failed to create OPAQUE registration response.");
        var config = KSFConfig.Create(KSFConfigType.MemoryConstrained, null, null, null);
        if (!client.FinishRegistration(password, response, started.ClientRegistrationState, OpaqueClientIdentifier, OpaqueServerIdentifier, config, out var finished) || finished is null) throw new CryptographicException("Failed to finish OPAQUE registration.");
        return new OpaqueIdentity(setup, finished.RegistrationRecord);
    }
    private static string ProtectOpaqueIdentity(OpaqueIdentity identity)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(identity);
        return Convert.ToBase64String(ProtectedData.Protect(json, OpaqueEntropy, DataProtectionScope.LocalMachine));
    }
    public const string OpaqueUserIdentifier = "PersonalRemoteDesktop.WindowsHost";
    public const string OpaqueClientIdentifier = "PersonalRemoteDesktop.WindowsClient";
    public const string OpaqueServerIdentifier = "PersonalRemoteDesktop.WindowsServer";
    private static readonly byte[] OpaqueEntropy = System.Text.Encoding.UTF8.GetBytes("PersonalRemoteDesktop/OPAQUE/v1");
}

public sealed record OpaqueIdentity(string ServerSetup, string RegistrationRecord);
