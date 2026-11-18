using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServiceLib.Helper;

namespace v2rayN.Services;

public sealed class MoghimiQuotaStatus
{
    public bool AccessAllowed { get; init; }
    public long FreeLimitBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long FreeUsedBytes { get; init; }
    public long FreeRemainingBytes { get; init; }
    public long PaidGrantedBytes { get; init; }
    public long PaidUsedBytes { get; init; }
    public long PaidRemainingBytes { get; init; }
    public string FreeMonthStart { get; init; } = "";
    public string Status { get; init; } = "unknown";
    public long TotalRemainingBytes => Math.Max(0, FreeRemainingBytes) + Math.Max(0, PaidRemainingBytes);
}

public sealed class MoghimiDeviceService : IDisposable
{
    public const string SubscriptionId = "MOGHIMI-CLOUD";
    private const string ApiUrl = "https://hxfnoaeyjnkecegpppcy.supabase.co/functions/v1/windows-api";
    private const string PublishableKey = "sb_publishable_nqjUZPJ_cr_SfzDntkbsLg_v6oaJNqY";
    private const long DefaultFreeLimitBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxUsageChunkBytes = 128L * 1024 * 1024;
    private const int MaxResponseBytes = 20 * 1024 * 1024;

    private static readonly HttpClient Http = new(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(7),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private sealed class PersistedAuth
    {
        public string InstallationId { get; set; } = Guid.NewGuid().ToString("D");
        public string? DeviceToken { get; set; }
        public string FreeDeviceToken { get; set; } =
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private readonly string _authPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MoghimiVPN",
        "windows-license.dpapi");
    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly SemaphoreSlim _reportGate = new(1, 1);
    private readonly PersistedAuth _auth;
    private readonly IDisposable _trafficSubscription;
    private Timer? _timer;
    private bool _registered;
    private bool _disposed;
    private MoghimiQuotaStatus? _status;
    private DateTimeOffset _statusAt;
    private readonly Guid _sessionId = Guid.NewGuid();
    private long _sessionRxBytes;
    private long _sessionTxBytes;
    private long _ackedRxBytes;
    private long _ackedTxBytes;
    private long _sequence;
    private long _pendingRxTarget;
    private long _pendingTxTarget;
    private long _pendingSequence;
    private bool _hasPendingUsage;
    private long _lastTrafficTick;

    public static MoghimiDeviceService Instance { get; } = new();

    public event Action<MoghimiQuotaStatus>? StatusChanged;
    public event Action<long, long>? TrafficChanged;
    public event Action<string>? AccessBlocked;

    public string DeviceDisplayId => _auth.InstallationId.Length >= 8
        ? _auth.InstallationId[..8].ToUpperInvariant()
        : _auth.InstallationId;

    public long SessionDownloadBytes => Interlocked.Read(ref _sessionRxBytes);
    public long SessionUploadBytes => Interlocked.Read(ref _sessionTxBytes);
    public MoghimiQuotaStatus? CurrentStatus => _status;

    private MoghimiDeviceService()
    {
        _auth = LoadOrCreateIdentity();
        _trafficSubscription = AppEvents.DispatcherTrafficBytesRequested
            .AsObservable()
            .Subscribe(OnTraffic);
    }

    public void Start()
    {
        if (_disposed || _timer is not null) return;
        _timer = new Timer(_ => _ = TimerTickAsync(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8));
        _ = RefreshStatusInBackgroundAsync();
    }

    private PersistedAuth LoadOrCreateIdentity()
    {
        try
        {
            if (File.Exists(_authPath))
            {
                var encrypted = File.ReadAllBytes(_authPath);
                var clear = ProtectedData.Unprotect(
                    encrypted,
                    Encoding.UTF8.GetBytes("MoghimiVPN|Windows|LicenseVault|v1"),
                    DataProtectionScope.CurrentUser);
                var existing = JsonSerializer.Deserialize<PersistedAuth>(clear)
                    ?? throw new InvalidDataException("شناسه دستگاه ویندوز قابل بازیابی نیست.");
                if (!Guid.TryParse(existing.InstallationId, out _) ||
                    existing.FreeDeviceToken.Length != 64 ||
                    !existing.FreeDeviceToken.All(Uri.IsHexDigit))
                {
                    throw new InvalidDataException("اطلاعات هویت دستگاه ویندوز معتبر نیست.");
                }
                return existing;
            }

            var auth = new PersistedAuth();
            PersistIdentity(auth);
            return auth;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or InvalidDataException)
        {
            throw new InvalidOperationException(
                "فایل هویت دستگاه ویندوز قابل خواندن نیست. برای جلوگیری از ایجاد سهمیه تازه، شناسه جدیدی جایگزین نشد.", ex);
        }
    }

    private void PersistIdentity(PersistedAuth auth)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_authPath)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(auth);
        var encrypted = ProtectedData.Protect(
            clear,
            Encoding.UTF8.GetBytes("MoghimiVPN|Windows|LicenseVault|v1"),
            DataProtectionScope.CurrentUser);
        var temporary = _authPath + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        if (File.Exists(_authPath)) File.Replace(temporary, _authPath, null);
        else File.Move(temporary, _authPath);
    }

    public async Task<bool> EnsureAccessAsync(CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        await EnsureRegisteredAsync(cancellationToken);
        var status = await GetStatusAsync(cancellationToken, forceRefresh);
        if (!status.AccessAllowed)
        {
            AccessBlocked?.Invoke("سهمیه رایگان ۸ گیگابایتی و حجم اشتراک خریداری‌شده این دستگاه به پایان رسیده است.");
            return false;
        }
        return true;
    }

    private async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        if (_registered) return;
        await _registrationGate.WaitAsync(cancellationToken);
        try
        {
            if (_registered) return;
            using var response = await PostAsync(new
            {
                action = "free_register",
                installation_id = _auth.InstallationId
            }, _auth.FreeDeviceToken, cancellationToken);

            if (!ReadBool(response.RootElement, "ok"))
                throw new InvalidOperationException("ثبت هویت دائمی دستگاه ویندوز در سرور انجام نشد.");

            _registered = true;
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    public async Task<MoghimiQuotaStatus> GetStatusAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (!forceRefresh && _status is not null &&
            DateTimeOffset.UtcNow - _statusAt < TimeSpan.FromSeconds(20))
            return _status;

        await _statusGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _status is not null &&
                DateTimeOffset.UtcNow - _statusAt < TimeSpan.FromSeconds(20))
                return _status;

            await EnsureRegisteredAsync(cancellationToken);
            using var response = await PostAsync(new { action = "free_status" }, _auth.FreeDeviceToken, cancellationToken);
            var status = ParseStatus(response.RootElement);
            SetStatus(status);
            return status;
        }
        finally
        {
            _statusGate.Release();
        }
    }

    public async Task ActivateSubscriptionAsync(string activationCode, CancellationToken cancellationToken = default)
    {
        var normalized = new string((activationCode ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (normalized.Length != 32)
            throw new InvalidOperationException("کد فعال‌سازی باید ۳۲ کاراکتر معتبر داشته باشد.");

        using var response = await PostAsync(new
        {
            action = "activate",
            activation_code = normalized,
            installation_id = _auth.InstallationId
        }, null, cancellationToken);

        if (!ReadBool(response.RootElement, "ok") ||
            !response.RootElement.TryGetProperty("device_token", out var tokenElement))
            throw new InvalidOperationException("سرور، فعال‌سازی اشتراک را تأیید نکرد.");

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new InvalidOperationException("توکن اشتراک دریافتی از سرور معتبر نیست.");

        _auth.DeviceToken = token;
        PersistIdentity(_auth);
        _status = null;
        await GetStatusAsync(cancellationToken, true);
    }

    public async Task<int> SyncCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureAccessAsync(cancellationToken, true))
            throw new InvalidOperationException("برای همگام‌سازی کانفیگ‌ها به سهمیه فعال ویندوز نیاز است.");

        using var response = await PostAsync(new { action = "profiles" }, _auth.FreeDeviceToken, cancellationToken);
        var root = response.RootElement;
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("فهرست سرورهای مقیمی از Supabase ساختار معتبری ندارد.");

        var configBlocks = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.TryGetProperty("config_text", out var configElement))
            {
                var config = configElement.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(config))
                    configBlocks.Add(config);
            }
        }

        if (configBlocks.Count == 0)
            throw new InvalidOperationException("در جدول سرورهای مقیمی هیچ کانفیگ فعالی برای همگام‌سازی وجود ندارد.");

        var configText = string.Join(Environment.NewLine, configBlocks);
        var config = AppManager.Instance.Config;
        var stagingId = "MOGHIMI-STAGING-" + Guid.NewGuid().ToString("N");

        var parsedCount = await ConfigHandler.AddBatchServers(config, configText, stagingId, true);
        var staged = await AppManager.Instance.ProfileItems(stagingId);
        if (parsedCount <= 0 || staged is not { Count: > 0 })
        {
            await ConfigHandler.RemoveServersViaSubid(config, stagingId, true);
            throw new InvalidOperationException("کانفیگ‌های Supabase دریافت شدند، اما هیچ‌کدام توسط موتور v2rayN قابل‌خواندن نبودند؛ فهرست قبلی حفظ شد.");
        }

        await ConfigHandler.RemoveServersViaSubid(config, SubscriptionId, true);
        foreach (var profile in staged)
        {
            profile.Subid = SubscriptionId;
            profile.IsSub = true;
        }
        await SQLiteHelper.Instance.UpdateAllAsync(staged);
        await ConfigHandler.RemoveServersViaSubid(config, stagingId, true);

        var subItem = await AppManager.Instance.GetSubItem(SubscriptionId);
        if (subItem is null)
        {
            await ConfigHandler.AddSubItem(config, new SubItem
            {
                Id = SubscriptionId,
                Remarks = "Moghimi Cloud",
                Url = ApiUrl + "?action=profiles",
                Enabled = false,
                AutoUpdateInterval = 0,
                RequestHeaders = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["apikey"] = PublishableKey
                }),
                Memo = "Managed by Moghimi device sync. Use the Moghimi Sync button."
            });
        }

        return staged.Count;
    }

    private void OnTraffic(ServerSpeedItem update)
    {
        if (update.ProxyDownBytes > 0) Interlocked.Add(ref _sessionRxBytes, update.ProxyDownBytes);
        if (update.ProxyUpBytes > 0) Interlocked.Add(ref _sessionTxBytes, update.ProxyUpBytes);
        if (update.ProxyDownBytes <= 0 && update.ProxyUpBytes <= 0) return;

        Interlocked.Exchange(ref _lastTrafficTick, Environment.TickCount64);
        TrafficChanged?.Invoke(SessionDownloadBytes, SessionUploadBytes);
    }

    private async Task TimerTickAsync()
    {
        if (_disposed) return;
        try
        {
            await ReportUsageAsync(CancellationToken.None);
            if (DateTimeOffset.UtcNow - _statusAt > TimeSpan.FromSeconds(45))
            {
                var allowed = await EnsureAccessAsync(CancellationToken.None, true);
                if (!allowed)
                    AccessBlocked?.Invoke("حجم قابل استفاده این دستگاه تمام شده است؛ اتصال در حال متوقف شدن است.");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MoghimiUsage", ex);
        }
    }

    private async Task ReportUsageAsync(CancellationToken cancellationToken)
    {
        await _reportGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureRegisteredAsync(cancellationToken);
            while (true)
            {
                if (!_hasPendingUsage)
                {
                    var totalRx = SessionDownloadBytes;
                    var totalTx = SessionUploadBytes;
                    var deltaRx = Math.Max(0, totalRx - _ackedRxBytes);
                    var deltaTx = Math.Max(0, totalTx - _ackedTxBytes);
                    if (deltaRx + deltaTx <= 0) break;

                    var addRx = Math.Min(deltaRx, MaxUsageChunkBytes);
                    var addTx = Math.Min(deltaTx, MaxUsageChunkBytes - addRx);
                    _pendingRxTarget = _ackedRxBytes + addRx;
                    _pendingTxTarget = _ackedTxBytes + addTx;
                    _pendingSequence = _sequence + 1;
                    _hasPendingUsage = true;
                }

                using var response = await PostAsync(new
                {
                    action = "free_usage",
                    session_id = _sessionId.ToString("D"),
                    sequence = _pendingSequence,
                    rx_total = _pendingRxTarget,
                    tx_total = _pendingTxTarget
                }, _auth.FreeDeviceToken, cancellationToken);

                if (!ReadBool(response.RootElement, "ok"))
                    throw new InvalidOperationException("ثبت مصرف در سرور ویندوز تأیید نشد.");

                // Keep the exact in-flight checkpoint on network failure. If the server
                // committed but the response was lost, its duplicate response safely
                // confirms this same sequence without charging the bytes twice.
                _ackedRxBytes = _pendingRxTarget;
                _ackedTxBytes = _pendingTxTarget;
                _sequence = _pendingSequence;
                _hasPendingUsage = false;

                var status = ParseStatus(response.RootElement);
                SetStatus(status);
                if (!status.AccessAllowed)
                {
                    AccessBlocked?.Invoke("سهمیه رایگان و حجم اشتراک خریداری‌شده تمام شده است.");
                    break;
                }
            }
        }
        finally
        {
            _reportGate.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        try
        {
            await ReportUsageAsync(new CancellationTokenSource(TimeSpan.FromSeconds(8)).Token);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MoghimiUsageShutdown", ex);
        }
    }

    private async Task RefreshStatusInBackgroundAsync()
    {
        try { await EnsureAccessAsync(CancellationToken.None, true); }
        catch (Exception ex) { Logging.SaveLog("MoghimiStatusStartup", ex); }
    }

    private async Task<JsonDocument> PostAsync(
        object payload,
        string? deviceToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd("Moghimi-v2rayN-Windows/1.0");
        request.Headers.TryAddWithoutValidation("apikey", PublishableKey);
        if (!string.IsNullOrWhiteSpace(deviceToken))
            request.Headers.TryAddWithoutValidation("x-moghimi-device-token", deviceToken);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            throw new InvalidOperationException("پاسخ Supabase بیش از اندازه مجاز است.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("پاسخ Supabase بیش از اندازه مجاز است.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        var document = JsonDocument.Parse(buffer.ToArray());
        if (!response.IsSuccessStatusCode)
        {
            var code = ReadString(document.RootElement, "code") ?? "service_unavailable";
            var message = ReadString(document.RootElement, "message");
            document.Dispose();
            throw new InvalidOperationException(message ?? code switch
            {
                "quota_exhausted" => "سهمیه رایگان و حجم اشتراک خریداری‌شده تمام شده است.",
                "device_not_registered" => "شناسه دائمی دستگاه در سرور ثبت نشده است.",
                "device_blocked" => "دسترسی این دستگاه مسدود شده است.",
                _ => "ارتباط با سرویس مقیمی برقرار نشد."
            });
        }
        return document;
    }

    private static MoghimiQuotaStatus ParseStatus(JsonElement root)
    {
        var freeLimit = ReadInt64(root, "free_limit_bytes");
        if (freeLimit <= 0) freeLimit = DefaultFreeLimitBytes;
        var freeUsed = Math.Max(0, ReadInt64(root, "free_used_bytes"));
        var freeRemaining = Math.Clamp(ReadInt64(root, "free_remaining_bytes"), 0, freeLimit);
        var paidGranted = Math.Max(0, ReadInt64(root, "paid_granted_bytes"));
        var paidUsed = Math.Max(0, ReadInt64(root, "paid_used_bytes"));
        var paidRemaining = Math.Max(0, ReadInt64(root, "paid_remaining_bytes"));

        return new MoghimiQuotaStatus
        {
            AccessAllowed = ReadBool(root, "access_allowed"),
            FreeLimitBytes = freeLimit,
            FreeUsedBytes = freeUsed,
            FreeRemainingBytes = freeRemaining,
            PaidGrantedBytes = paidGranted,
            PaidUsedBytes = paidUsed,
            PaidRemainingBytes = paidRemaining,
            FreeMonthStart = ReadString(root, "free_month_start") ?? "",
            Status = ReadString(root, "status") ?? (freeRemaining + paidRemaining > 0 ? "active" : "exhausted")
        };
    }

    private void SetStatus(MoghimiQuotaStatus status)
    {
        _status = status;
        _statusAt = DateTimeOffset.UtcNow;
        StatusChanged?.Invoke(status);
    }

    private static bool ReadBool(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    private static long ReadInt64(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number)) return number;
        return 0;
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
        _trafficSubscription.Dispose();
        _registrationGate.Dispose();
        _statusGate.Dispose();
        _reportGate.Dispose();
    }
}
