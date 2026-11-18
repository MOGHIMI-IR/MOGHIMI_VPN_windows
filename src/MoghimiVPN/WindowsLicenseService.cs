using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoghimiVPN;

public sealed class WindowsReleaseInfo
{
    public string VersionName { get; init; } = "";
    public long VersionCode { get; init; }
    public string ReleaseChannel { get; init; } = "stable";
    public string InstallerUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long FileSize { get; init; }
    public string ReleaseNotes { get; init; } = "";
    public bool IsMandatory { get; init; }
    public string? MinimumSupportedVersion { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

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
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private WindowsLicenseStatus? _cachedStatus;
    private DateTimeOffset _cachedStatusAt;

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
                    "quota_exhausted" => "حجم اشتراک ویندوز تمام شده است.",
                    "stale_or_non_monotonic_usage" => "گزارش مصرف تکراری بود؛ گزارش بعدی مجدداً ارسال می‌شود.",
                    "account_blocked" => "دسترسی این حساب مسدود شده است.",
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


    public async Task<WindowsReleaseInfo?> GetLatestReleaseAsync(
        CancellationToken cancellationToken = default)
    {
        using var document = await PostAsync(new { action = "latest_release" }, null, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException("اطلاعات نسخه ویندوز از سرور معتبر نیست.");
        if (!root.TryGetProperty("available", out var available) || !available.GetBoolean())
            return null;
        if (!root.TryGetProperty("release", out var release) || release.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("اطلاعات انتشار ویندوز ناقص است.");

        var info = new WindowsReleaseInfo
        {
            VersionName = ReadString(release, "version_name") ?? "",
            VersionCode = ReadInt64(release, "version_code"),
            ReleaseChannel = ReadString(release, "release_channel") ?? "stable",
            InstallerUrl = ReadString(release, "installer_url") ?? "",
            Sha256 = ReadString(release, "sha256") ?? "",
            FileSize = ReadInt64(release, "file_size"),
            ReleaseNotes = ReadString(release, "release_notes") ?? "",
            IsMandatory = release.TryGetProperty("is_mandatory", out var mandatory) &&
                mandatory.ValueKind == JsonValueKind.True,
            MinimumSupportedVersion = ReadString(release, "minimum_supported_version"),
            PublishedAt = ReadDate(release, "published_at")
        };

        if (string.IsNullOrWhiteSpace(info.VersionName) || info.VersionCode <= 0 ||
            !Uri.TryCreate(info.InstallerUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, new Uri(ApiUrl).Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/storage/v1/object/public/windows-releases/", StringComparison.Ordinal) ||
            !System.Text.RegularExpressions.Regex.IsMatch(info.Sha256, "^[a-fA-F0-9]{64}$") ||
            info.FileSize <= 0 || info.FileSize > 1024L * 1024 * 1024)
            throw new InvalidOperationException("نشانی، اندازه یا امضای Hash فایل نصب ویندوز معتبر نیست.");

        return info;
    }

    public async Task<string> DownloadAndVerifyInstallerAsync(
        WindowsReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(release.InstallerUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, new Uri(ApiUrl).Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/storage/v1/object/public/windows-releases/", StringComparison.Ordinal) ||
            !System.Text.RegularExpressions.Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$") ||
            release.FileSize <= 0 || release.FileSize > 1024L * 1024 * 1024)
            throw new InvalidOperationException("آدرس یا مشخصات فایل نصب‌کننده مجاز نیست.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("MoghimiVPN-Windows/1.0");
        using var response = await Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(finalUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(finalUri.AbsolutePath, uri.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidOperationException("دانلود به میزبان یا مسیر دیگری هدایت شد و مسدود شد.");
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength &&
            (contentLength != release.FileSize || contentLength > 1024L * 1024 * 1024))
            throw new InvalidOperationException("اندازه فایل دانلودی با اطلاعات انتشار یکسان نیست.");

        var safeVersion = new string(release.VersionName
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')
            .ToArray());
        var directory = Path.Combine(Path.GetTempPath(), "MoghimiVPN", "Updates");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"MoghimiVPN-Windows-{safeVersion}-Setup.exe");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".download";

        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > release.FileSize || total > 1024L * 1024 * 1024)
                    throw new InvalidOperationException("حجم فایل دانلودی بیش از مقدار ثبت‌شده است.");
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                progress?.Report(release.FileSize > 0 ? (double)total / release.FileSize : 0);
            }
            await output.FlushAsync(cancellationToken);

            var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (total != release.FileSize ||
                !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(hash), Convert.FromHexString(release.Sha256.ToLowerInvariant())))
                throw new InvalidOperationException("فایل نصب دانلود شد، اما SHA-256 آن با نسخه ثبت‌شده مطابقت ندارد.");

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temporary, destination);
            return destination;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
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
