using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoghimiVPN;

public sealed class WindowsUsageReport
{
    public long UsedBytes { get; init; }
    public long RemainingBytes { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class WindowsLicenseStatus
{
    public bool Active { get; init; }
    public long GrantedBytes { get; init; }
    public long UsedBytes { get; init; }
    public long RemainingBytes { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string Status { get; init; } = "not_activated";
}

public sealed class WindowsLicenseService : IWindowsCloudCatalog
{
    private const string ApiUrl = "https://hxfnoaeyjnkecegpppcy.supabase.co/functions/v1/windows-api";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Client = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
    {
        Timeout = TimeSpan.FromSeconds(18)
    };

    private sealed class PersistedAuth
    {
        public string InstallationId { get; set; } = Guid.NewGuid().ToString();
        public string? DeviceToken { get; set; }
    }

    private readonly string _authPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MoghimiVPN",
        "windows-license.dpapi");
    private PersistedAuth _auth;

    public string? DeviceToken => _auth.DeviceToken;
    public bool HasActivation => !string.IsNullOrWhiteSpace(_auth.DeviceToken);

    public WindowsLicenseService()
    {
        _auth = Load();
        if (string.IsNullOrWhiteSpace(_auth.InstallationId))
            _auth.InstallationId = Guid.NewGuid().ToString();
        Save();
    }

    private PersistedAuth Load()
    {
        try
        {
            if (!File.Exists(_authPath)) return new PersistedAuth();
            var encrypted = File.ReadAllBytes(_authPath);
            var clear = ProtectedData.Unprotect(encrypted, Entropy(), DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<PersistedAuth>(clear) ?? new PersistedAuth();
        }
        catch
        {
            // Keep an unreadable file for recovery; do not silently erase the device identity.
            return new PersistedAuth();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_authPath)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(_auth);
        var encrypted = ProtectedData.Protect(clear, Entropy(), DataProtectionScope.CurrentUser);
        var temporary = _authPath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        if (File.Exists(_authPath)) File.Replace(temporary, _authPath, null);
        else File.Move(temporary, _authPath);
    }

    private static byte[] Entropy() =>
        Encoding.UTF8.GetBytes("MoghimiVPN|Windows|LicenseVault|v1");

    private async Task<JsonDocument> PostAsync(
        object payload,
        string? deviceToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd("MoghimiVPN-Windows/1.0");
        if (!string.IsNullOrWhiteSpace(deviceToken))
            request.Headers.TryAddWithoutValidation("x-moghimi-device-token", deviceToken);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var finalUri = response.RequestMessage?.RequestUri;
        var expected = new Uri(ApiUrl);
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(finalUri.Host, expected.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("پاسخ سرویس ویندوز از میزبان معتبر دریافت نشد.");

        if (response.Content.Headers.ContentLength is long size && size > MaxResponseBytes)
            throw new InvalidOperationException("پاسخ سرویس ویندوز بیش از اندازه مجاز است.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("پاسخ سرویس ویندوز بیش از اندازه مجاز است.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        var document = JsonDocument.Parse(buffer.ToArray());
        if (!response.IsSuccessStatusCode)
        {
            var message = document.RootElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            var code = document.RootElement.TryGetProperty("code", out var codeElement)
                ? codeElement.GetString()
                : null;
            document.Dispose();
            throw new InvalidOperationException(!string.IsNullOrWhiteSpace(message)
                ? message
                : code switch
                {
                    "device_not_registered" => "این دستگاه فعال‌سازی معتبر ندارد؛ کد فعال‌سازی را دوباره ثبت کن.",
                    "subscription_inactive" => "اشتراک ویندوز فعال نیست یا اعتبار آن به پایان رسیده است.",
                    _ => "ارتباط با سرویس ویندوز ناموفق بود. اتصال اینترنت را بررسی کن."
                });
        }
        return document;
    }

    public async Task<WindowsLicenseStatus> ActivateAsync(
        string activationCode,
        CancellationToken cancellationToken = default)
    {
        using var document = await PostAsync(new
        {
            action = "activate",
            activation_code = activationCode,
            installation_id = _auth.InstallationId
        }, null, cancellationToken);

        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean() ||
            !root.TryGetProperty("device_token", out var tokenElement))
            throw new InvalidOperationException("فعال‌سازی از طرف سرور تأیید نشد.");

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64)
            throw new InvalidOperationException("پاسخ فعال‌سازی معتبر نیست.");

        _auth.DeviceToken = token;
        _cachedStatus = null;
        Save();
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<WindowsLicenseStatus> GetStatusAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (!HasActivation)
            return new WindowsLicenseStatus { Active = false, Status = "not_activated" };

        if (!forceRefresh && _cachedStatus is not null &&
            DateTimeOffset.UtcNow - _cachedStatusAt < TimeSpan.FromSeconds(20))
            return _cachedStatus;

        await _statusGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _cachedStatus is not null &&
                DateTimeOffset.UtcNow - _cachedStatusAt < TimeSpan.FromSeconds(20))
                return _cachedStatus;

            using var document = await PostAsync(new { action = "status" }, DeviceToken, cancellationToken);
            var root = document.RootElement;
            var status = new WindowsLicenseStatus
            {
                Active = root.TryGetProperty("active", out var active) && active.GetBoolean(),
                GrantedBytes = ReadInt64(root, "granted_bytes"),
                UsedBytes = ReadInt64(root, "used_bytes"),
                RemainingBytes = ReadInt64(root, "remaining_bytes"),
                ExpiresAt = ReadDate(root, "expires_at"),
                Status = ReadString(root, "status") ?? ReadString(root, "code") ?? "unknown"
            };
            _cachedStatus = status;
            _cachedStatusAt = DateTimeOffset.UtcNow;
            return status;
        }
        finally
        {
            _statusGate.Release();
        }
    }


    public async Task<WindowsUsageReport> ReportUsageAsync(
        Guid sessionId,
        long sequence,
        long downloadBytes,
        long uploadBytes,
        CancellationToken cancellationToken = default)
    {
        if (!HasActivation) throw new InvalidOperationException("دستگاه ویندوز فعال‌سازی نشده است.");
        if (sessionId == Guid.Empty || sequence < 1 || downloadBytes < 0 || uploadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), "گزارش مصرف معتبر نیست.");

        using var document = await PostAsync(new
        {
            action = "usage",
            session_id = sessionId.ToString(),
            sequence,
            rx_bytes = downloadBytes,
            tx_bytes = uploadBytes
        }, DeviceToken, cancellationToken);

        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var code = ReadString(root, "code") ?? "usage_report_failed";
            throw new InvalidOperationException(code switch
            {
                "quota_exhausted" => "حجم اشتراک ویندوز تمام شده است.",
                "subscription_inactive" => "اشتراک ویندوز فعال نیست.",
                "stale_or_non_monotonic_usage" => "گزارش مصرف تکراری بود و دوباره ارسال می‌شود.",
                _ => "ثبت مصرف اشتراک موقتاً انجام نشد."
            });
        }

        var result = new WindowsUsageReport
        {
            UsedBytes = ReadInt64(root, "used_bytes"),
            RemainingBytes = ReadInt64(root, "remaining_bytes"),
            ExpiresAt = ReadDate(root, "expires_at")
        };
        if (_cachedStatus is not null)
        {
            _cachedStatus = new WindowsLicenseStatus
            {
                Active = result.RemainingBytes > 0 && (result.ExpiresAt is null || result.ExpiresAt > DateTimeOffset.UtcNow),
                GrantedBytes = _cachedStatus.GrantedBytes,
                UsedBytes = result.UsedBytes,
                RemainingBytes = result.RemainingBytes,
                ExpiresAt = result.ExpiresAt ?? _cachedStatus.ExpiresAt,
                Status = result.RemainingBytes <= 0 ? "exhausted" : "active"
            };
            _cachedStatusAt = DateTimeOffset.UtcNow;
        }
        return result;
    }

    public async Task<List<ProxyProfile>> FetchMoghimiCloudProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!HasActivation)
            throw new InvalidOperationException("برای دریافت سرورهای اختصاصی مقیمی، ابتدا کد فعال‌سازی ویندوز را ثبت کن.");

        using var document = await PostAsync(new { action = "profiles" }, DeviceToken, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("فهرست سرورهای ویندوز معتبر نیست.");

        if (entries.GetArrayLength() > 2000)
            throw new InvalidOperationException("تعداد سرورهای دریافتی از سقف مجاز بیشتر است.");

        var profiles = new List<ProxyProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = entry.TryGetProperty("title", out var titleValue)
                ? titleValue.GetString()
                : null;
            var payload = entry.TryGetProperty("config_text", out var configValue)
                ? configValue.GetString() ?? ""
                : "";

            totalBytes += Encoding.UTF8.GetByteCount(payload);
            if (totalBytes > 16 * 1024 * 1024)
                throw new InvalidOperationException("حجم فهرست سرورها از سقف ۱۶ مگابایت بیشتر است؛ فهرست قبلی حفظ شد.");

            foreach (var profile in ProfileCodec.DecodeMany(payload))
            {
                if (profile.Name == "سرور جدید" && !string.IsNullOrWhiteSpace(title))
                    profile.Name = title;
                profile.SubscriptionId = "moghimi-cloud";
                var key = $"{profile.Protocol}|{profile.Server}|{profile.Port}|{profile.Uuid}|{profile.Password}";
                if (seen.Add(key))
                    profiles.Add(profile);
                if (profiles.Count > 2000)
                    throw new InvalidOperationException("تعداد کانفیگ‌های معتبر از سقف مجاز بیشتر است.");
            }
        }

        return profiles;
    }

    private static long ReadInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        DateTimeOffset.TryParse(ReadString(element, property), out var value) ? value : null;
}
