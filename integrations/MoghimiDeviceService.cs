using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ServiceLib.Handler.Data;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Models.Entities;

// This integration is compiled into the Windows WPF flavor only. ServiceLib itself
// is shared by upstream targets, so platform analyzers cannot infer the flavor gate.
#pragma warning disable CA1416

namespace ServiceLib.Manager;

public sealed class MoghimiDeviceStatus
{
    public bool AccessAllowed { get; init; }
    public long FreeLimitBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long FreeUsedBytes { get; init; }
    public long FreeRemainingBytes { get; init; } = 8L * 1024 * 1024 * 1024;
    public long PaidGrantedBytes { get; init; }
    public long PaidUsedBytes { get; init; }
    public long PaidRemainingBytes { get; init; }
    public string Status { get; init; } = "unknown";
    public DateTimeOffset? PaidExpiresAt { get; init; }
    public double FreePercent => FreeLimitBytes <= 0 ? 0 : Math.Clamp(100d * FreeUsedBytes / FreeLimitBytes, 0, 100);
}

public sealed class MoghimiDeviceService
{
    private const string ApiUrl = "https://hxfnoaeyjnkecegpppcy.supabase.co/functions/v1/windows-api";
    private const string CloudGroupId = "moghimi-cloud";
    private const long MaxReportChunkBytes = 128L * 1024 * 1024;
    private const int MaxResponseBytes = 20 * 1024 * 1024;
    private static readonly byte[] MachineEntropy = Encoding.UTF8.GetBytes("MoghimiVPN|Windows|MachineDeviceVault|v1");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("MoghimiVPN|Windows|LicenseVault|v1");
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(25) };

    private sealed class DeviceState
    {
        public string InstallationId { get; set; } = "";
        public string FreeDeviceToken { get; set; } = "";
        public string? PaidDeviceToken { get; set; }
    }

    private static readonly Lazy<MoghimiDeviceService> LazyInstance = new(() => new MoghimiDeviceService());
    public static MoghimiDeviceService Instance => LazyInstance.Value;

    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "MoghimiVPN",
        "device-auth.dpapi");
    private readonly string _userStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MoghimiVPN",
        "device-auth-machine-fallback.dpapi");
    private bool _useUserStateFallback;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly SemaphoreSlim _reportGate = new(1, 1);
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private readonly object _trafficLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private DeviceState _state;
    private MoghimiDeviceStatus? _lastStatus;
    private DateTimeOffset _lastStatusRefreshUtc;
    private bool _initialized;
    private Task? _usageLoop;
    private Guid _sessionId;
    private long _sequence;
    private long _reportedRx;
    private long _reportedTx;
    private long _pendingRx;
    private long _pendingTx;

    public event EventHandler<MoghimiDeviceStatus>? StatusChanged;

    private MoghimiDeviceService()
    {
        _state = LoadState();
        if (!Guid.TryParse(_state.InstallationId, out _))
            _state.InstallationId = CreateStableInstallationId();
        if (!IsToken(_state.FreeDeviceToken))
            _state.FreeDeviceToken = RandomToken();
        SaveState();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            await EnsureRegisteredAsync(cancellationToken);
            await EnsureCloudGroupAsync();
            var status = await GetStatusAsync(forceRefresh: true, cancellationToken);
            PublishStatus(status);

            _sessionId = Guid.NewGuid();
            _sequence = 0;
            _reportedRx = 0;
            _reportedTx = 0;
            _initialized = true;
            _usageLoop = Task.Run(() => UsageLoopAsync(_lifetime.Token));
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<MoghimiDeviceStatus> GetStatusAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureRegisteredAsync(cancellationToken);
        if (!forceRefresh && _lastStatus is not null) return _lastStatus;

        await _statusGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _lastStatus is not null) return _lastStatus;
            using var document = await PostAsync(new { action = "free_status" }, _state.FreeDeviceToken, cancellationToken);
            var status = ParseStatus(document.RootElement);
            PublishStatus(status);
            _lastStatusRefreshUtc = DateTimeOffset.UtcNow;
            return status;
        }
        finally
        {
            _statusGate.Release();
        }
    }

    public async Task<bool> EnsureAccessAllowedAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var status = await GetStatusAsync(forceRefresh: true, cancellationToken);
        if (status.AccessAllowed) return true;
        await StopForQuotaAsync();
        return false;
    }

    public void AccumulateTraffic(long downloadKilobytes, long uploadKilobytes)
    {
        if (downloadKilobytes <= 0 && uploadKilobytes <= 0) return;

        // v2rayN's sing-box statistics are decimal KB; Xray statistics are KiB.
        var multiplier = AppManager.Instance.IsRunningCore(ECoreType.sing_box) ? 1000L : 1024L;
        var rx = SafeMultiply(Math.Max(0, downloadKilobytes), multiplier);
        var tx = SafeMultiply(Math.Max(0, uploadKilobytes), multiplier);

        lock (_trafficLock)
        {
            _pendingRx = SafeAdd(_pendingRx, rx);
            _pendingTx = SafeAdd(_pendingTx, tx);
        }
    }

    public async Task<int> SyncProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!await EnsureAccessAllowedAsync(cancellationToken))
            throw new InvalidOperationException("حجم رایگان و اشتراک این دستگاه تمام شده است.");

        using var document = await PostAsync(new { action = "profiles" }, _state.FreeDeviceToken, cancellationToken);
        if (!document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("فهرست سرورهای Moghimi از سرور معتبر نیست.");

        var builder = new StringBuilder();
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("config_text", out var configElement) ||
                configElement.ValueKind != JsonValueKind.String)
                continue;

            var value = configElement.GetString();
            if (string.IsNullOrWhiteSpace(value)) continue;
            builder.AppendLine(value.Trim());
        }

        if (builder.Length == 0)
            throw new InvalidOperationException("در جدول سرورهای ویندوز هنوز کانفیگ معتبری وجود ندارد.");

        var stageId = "moghimi-stage-" + Guid.NewGuid().ToString("N");
        var imported = await ConfigDbHandler.AddBatchServers(builder.ToString(), stageId, isSub: true);
        var stagedProfiles = await ConfigDbQuery.ProfileItems(stageId) ?? [];
        if (imported <= 0 || stagedProfiles.Count == 0)
        {
            await ConfigDbHandler.RemoveServersViaSubid(stageId, isSub: true);
            throw new InvalidOperationException("هیچ کانفیگ معتبری از فهرست ابری وارد نشد؛ سرورهای قبلی حفظ شدند.");
        }

        await EnsureCloudGroupAsync();

        // Import into a staging group first. Only replace the visible cloud set after
        // at least one valid profile has been parsed and the new group assignment succeeds.
        var oldProfiles = await ConfigDbQuery.ProfileItems(CloudGroupId) ?? [];
        await ConfigDbHandler.MoveToGroup(stagedProfiles, CloudGroupId);
        if (oldProfiles.Count > 0)
            await ConfigDbHandler.RemoveServers(oldProfiles);

        await ConfigDbHandler.RemoveServersViaSubid(stageId, isSub: true);

        // Make the Moghimi cloud group visible immediately after a successful sync.
        ConfigTextHandler.Config.SubIndexId = CloudGroupId;
        await ConfigTextHandler.SaveConfig();
        return stagedProfiles.Count;
    }

    public async Task FlushUsageAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized) return;
        try
        {
            await ReportUsageOrRefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MoghimiDeviceService.FlushUsage", ex);
        }
    }

    public async Task ActivateAsync(string activationCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(activationCode))
            throw new ArgumentException("کد فعال‌سازی خالی است.", nameof(activationCode));

        using var document = await PostAsync(new
        {
            action = "activate",
            activation_code = activationCode.Trim(),
            installation_id = _state.InstallationId
        }, null, cancellationToken);

        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("device_token", out var tokenElement))
            throw new InvalidOperationException("سرور، فعال‌سازی اشتراک ویندوز را تأیید نکرد.");

        var token = tokenElement.GetString();
        if (!IsToken(token))
            throw new InvalidOperationException("توکن دریافتی برای فعال‌سازی معتبر نیست.");

        _state.PaidDeviceToken = token;
        SaveState();

        var status = await GetStatusAsync(forceRefresh: true, cancellationToken);
        PublishStatus(status);
    }

    private async Task EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        using var document = await PostAsync(new
        {
            action = "free_register",
            installation_id = _state.InstallationId
        }, _state.FreeDeviceToken, cancellationToken);

        var root = document.RootElement;
        if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
        {
            var code = ReadString(root, "code");
            throw new InvalidOperationException(code == "device_blocked"
                ? "دسترسی این دستگاه به سرویس مقیمی مسدود شده است."
                : "ثبت دستگاه ویندوز در سرویس مقیمی انجام نشد.");
        }
    }

    private async Task EnsureCloudGroupAsync()
    {
        var existing = await ConfigDbQuery.GetSubItem(CloudGroupId);
        if (existing is not null) return;

        await ConfigDbHandler.AddSubItem(new SubItem
        {
            Id = CloudGroupId,
            Remarks = "سرورهای Moghimi",
            Url = "",
            MoreUrl = "",
            Enabled = false,
            Sort = -100
        });
    }

    private async Task UsageLoopAsync(CancellationToken cancellationToken)
    {
        // Report traffic frequently enough that high-throughput connections cannot
        // build an unbounded in-memory usage backlog between quota checks.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
                await ReportUsageOrRefreshAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logging.SaveLog("MoghimiDeviceService.UsageLoop", ex);
            }
        }
    }

    private async Task ReportUsageOrRefreshAsync(CancellationToken cancellationToken)
    {
        await _reportGate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                long pendingRx;
                long pendingTx;
                lock (_trafficLock)
                {
                    pendingRx = _pendingRx;
                    pendingTx = _pendingTx;
                }

                if (pendingRx <= 0 && pendingTx <= 0)
                {
                    // Do not poll Supabase every five seconds while the tunnel is idle.
                    // Active traffic is still reported on every timer tick.
                    if (_lastStatus is null ||
                        DateTimeOffset.UtcNow - _lastStatusRefreshUtc >= TimeSpan.FromSeconds(30))
                    {
                        var fresh = await GetStatusAsync(forceRefresh: true, cancellationToken);
                        if (!fresh.AccessAllowed) await StopForQuotaAsync();
                    }
                    return;
                }

                // The server accepts deltas up to 256 MiB. Use 128 MiB chunks and
                // drain the entire backlog in this pass instead of sending just one
                // chunk every timer interval.
                var chunkRx = Math.Min(pendingRx, MaxReportChunkBytes);
                var chunkTx = Math.Min(pendingTx, Math.Max(0, MaxReportChunkBytes - chunkRx));
                var nextRx = SafeAdd(_reportedRx, chunkRx);
                var nextTx = SafeAdd(_reportedTx, chunkTx);
                var nextSequence = _sequence + 1;

                using var document = await PostAsync(new
                {
                    action = "free_usage",
                    session_id = _sessionId.ToString(),
                    sequence = nextSequence,
                    rx_total = nextRx,
                    tx_total = nextTx
                }, _state.FreeDeviceToken, cancellationToken);

                var root = document.RootElement;
                if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                    throw new InvalidOperationException("گزارش مصرف ویندوز از طرف سرور پذیرفته نشد.");

                _sequence = nextSequence;
                _reportedRx = nextRx;
                _reportedTx = nextTx;
                lock (_trafficLock)
                {
                    _pendingRx = Math.Max(0, _pendingRx - chunkRx);
                    _pendingTx = Math.Max(0, _pendingTx - chunkTx);
                }

                var status = ParseStatus(root);
                PublishStatus(status);
                _lastStatusRefreshUtc = DateTimeOffset.UtcNow;
                if (!status.AccessAllowed)
                {
                    await StopForQuotaAsync();
                    return;
                }
            }
        }
        finally
        {
            _reportGate.Release();
        }
    }

    private async Task StopForQuotaAsync()
    {
        if (!await _stopGate.WaitAsync(0)) return;
        try
        {
            await CoreManager.Instance.CoreStop();
            await SysProxyHandler.UpdateSysProxy(ConfigTextHandler.Config, forceDisable: true);
        }
        finally
        {
            _stopGate.Release();
        }
    }

    private static MoghimiDeviceStatus ParseStatus(JsonElement root)
    {
        var freeLimit = ReadInt64(root, "free_limit_bytes");
        if (freeLimit <= 0) freeLimit = 8L * 1024 * 1024 * 1024;
        var freeUsed = Math.Max(0, ReadInt64(root, "free_used_bytes"));
        var freeRemaining = Math.Clamp(ReadInt64(root, "free_remaining_bytes"), 0, freeLimit);
        var paidGranted = Math.Max(0, ReadInt64(root, "paid_granted_bytes"));
        var paidUsed = Math.Max(0, ReadInt64(root, "paid_used_bytes"));
        var paidRemaining = Math.Max(0, ReadInt64(root, "paid_remaining_bytes"));

        return new MoghimiDeviceStatus
        {
            AccessAllowed = root.TryGetProperty("access_allowed", out var access) && access.ValueKind == JsonValueKind.True,
            FreeLimitBytes = freeLimit,
            FreeUsedBytes = Math.Min(freeUsed, freeLimit),
            FreeRemainingBytes = freeRemaining,
            PaidGrantedBytes = paidGranted,
            PaidUsedBytes = paidUsed,
            PaidRemainingBytes = paidRemaining,
            Status = ReadString(root, "status") ?? (freeRemaining + paidRemaining > 0 ? "active" : "exhausted"),
            PaidExpiresAt = ReadDate(root, "paid_expires_at")
        };
    }

    private async Task<JsonDocument> PostAsync(object payload, string? deviceToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.UserAgent.ParseAdd("MoghimiVPN-Windows/2.0");
        if (!string.IsNullOrWhiteSpace(deviceToken))
            request.Headers.TryAddWithoutValidation("x-moghimi-device-token", deviceToken);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(finalUri.Host, new Uri(ApiUrl).Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("پاسخ سرویس مقیمی از میزبان مورد انتظار دریافت نشد.");

        if (response.Content.Headers.ContentLength is long length && length > MaxResponseBytes)
            throw new InvalidOperationException("پاسخ سرویس مقیمی بیش از اندازه مجاز است.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("پاسخ سرویس مقیمی بیش از اندازه مجاز است.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        var document = JsonDocument.Parse(buffer.ToArray());
        if (!response.IsSuccessStatusCode)
        {
            var message = ReadString(document.RootElement, "message");
            var code = ReadString(document.RootElement, "code");
            document.Dispose();
            throw new InvalidOperationException(!string.IsNullOrWhiteSpace(message)
                ? message
                : code switch
                {
                    "quota_exhausted" => "حجم رایگان و اشتراک این دستگاه تمام شده است.",
                    "device_not_registered" => "این دستگاه در سرویس مقیمی ثبت نشده است.",
                    "device_blocked" => "دسترسی این دستگاه مسدود شده است.",
                    "subscription_inactive" => "اشتراک ویندوز فعال نیست.",
                    _ => "ارتباط با سرویس ویندوز مقیمی ناموفق بود."
                });
        }
        return document;
    }

    private DeviceState LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var cipher = File.ReadAllBytes(_statePath);
                var clear = ProtectedData.Unprotect(cipher, MachineEntropy, DataProtectionScope.LocalMachine);
                var state = JsonSerializer.Deserialize<DeviceState>(clear);
                if (state is not null && Guid.TryParse(state.InstallationId, out _) && IsToken(state.FreeDeviceToken))
                    return state;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MoghimiDeviceService.LoadMachineState", ex);
        }

        // Standard-user installs may not have write access to ProgramData. Keep a DPAPI
        // CurrentUser fallback so normal users can still connect; the identity itself is
        // derived from the machine GUID and therefore does not depend on this fallback file.
        try
        {
            if (File.Exists(_userStatePath))
            {
                var cipher = File.ReadAllBytes(_userStatePath);
                var clear = ProtectedData.Unprotect(cipher, MachineEntropy, DataProtectionScope.CurrentUser);
                var state = JsonSerializer.Deserialize<DeviceState>(clear);
                if (state is not null && Guid.TryParse(state.InstallationId, out _) && IsToken(state.FreeDeviceToken))
                {
                    _useUserStateFallback = true;
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MoghimiDeviceService.LoadUserFallback", ex);
        }

        // Migrate the original per-user DPAPI file so existing beta installations retain their device id.
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MoghimiVPN",
                "windows-license.dpapi");
            if (File.Exists(legacyPath))
            {
                var cipher = File.ReadAllBytes(legacyPath);
                var clear = ProtectedData.Unprotect(cipher, LegacyEntropy, DataProtectionScope.CurrentUser);
                using var legacy = JsonDocument.Parse(clear);
                var root = legacy.RootElement;
                var installationId = ReadString(root, "InstallationId");
                var freeToken = ReadString(root, "FreeDeviceToken");
                var paidToken = ReadString(root, "DeviceToken");
                if (Guid.TryParse(installationId, out _) && IsToken(freeToken))
                {
                    _useUserStateFallback = true;
                    return new DeviceState { InstallationId = installationId!, FreeDeviceToken = freeToken!, PaidDeviceToken = paidToken };
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MoghimiDeviceService.MigrateLegacyState", ex);
        }

        return new DeviceState
        {
            InstallationId = CreateStableInstallationId(),
            FreeDeviceToken = RandomToken()
        };
    }

    private void SaveState()
    {
        if (!_useUserStateFallback)
        {
            try
            {
                WriteProtectedState(_statePath, DataProtectionScope.LocalMachine);
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                Logging.SaveLog("MoghimiDeviceService.ProgramDataUnavailable", ex);
                _useUserStateFallback = true;
            }
        }

        WriteProtectedState(_userStatePath, DataProtectionScope.CurrentUser);
    }

    private void WriteProtectedState(string path, DataProtectionScope scope)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(_state);
        var entropy = MachineEntropy;
        var cipher = ProtectedData.Protect(clear, entropy, scope);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, cipher);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private static string CreateStableInstallationId()
    {
        var machineGuid = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
            "MachineGuid",
            null) as string;

        if (!string.IsNullOrWhiteSpace(machineGuid))
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes("MoghimiVPN-Device|" + machineGuid));

            // Emit a deterministic RFC 4122 v5-shaped UUID so the API's UUID
            // version/variant validation accepts the stable machine identifier.
            digest[7] = (byte)((digest[7] & 0x0F) | 0x50);
            digest[8] = (byte)((digest[8] & 0x3F) | 0x80);
            return new Guid(digest.AsSpan(0, 16)).ToString("D").ToLowerInvariant();
        }

        return Guid.NewGuid().ToString("D");
    }

    private static string RandomToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static bool IsToken(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static long ReadInt64(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement element, string name) =>
        DateTimeOffset.TryParse(ReadString(element, name), out var value) ? value : null;

    private void PublishStatus(MoghimiDeviceStatus status)
    {
        _lastStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    private static long SafeMultiply(long value, long factor) =>
        value <= 0 ? 0 : value > long.MaxValue / factor ? long.MaxValue : value * factor;

    private static long SafeAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;
}
