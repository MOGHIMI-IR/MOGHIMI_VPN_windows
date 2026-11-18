using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MoghimiVPN;

public sealed class ConnectionManager : IDisposable
{
    private Process? _process;
    private bool _systemProxyEnabled;
    private int _systemProxyPort = 2080;
    private int _trafficApiPort;
    private string? _trafficApiSecret;
    private CancellationTokenSource? _trafficMonitorCts;
    private Task? _trafficMonitorTask;
    private long _uploadBytes;
    private long _downloadBytes;
    private static readonly HttpClient TrafficClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _enginePath = Path.Combine(AppContext.BaseDirectory, "runtime", "sing-box.exe");
    private readonly string _tunnelHostPath = Path.Combine(AppContext.BaseDirectory, "tunnel-host", "MoghimiVPN.TunnelHost.exe");
    public event EventHandler<string>? LogMessage;
    public event EventHandler? EngineExited;
    public bool IsRunning => _process is { } process && !HasExitedSafely(process);
    public (long UploadBytes, long DownloadBytes) GetTrafficSnapshot() =>
        (Interlocked.Read(ref _uploadBytes), Interlocked.Read(ref _downloadBytes));

    public async Task StartAsync(ProxyProfile profile, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("VPN از قبل در حال اجراست.");
        if (!File.Exists(_enginePath))
            throw new FileNotFoundException("هسته sing-box پیدا نشد. بسته نصب را کامل نصب یا دوباره دریافت کن.", _enginePath);
        if (!File.Exists(_tunnelHostPath))
            throw new FileNotFoundException("راه‌انداز اتصال پیدا نشد. پوشه tunnel-host را کامل نصب یا دوباره دریافت کن.", _tunnelHostPath);
        if (settings.ConnectionMode == "TUN" && !File.Exists(Path.Combine(AppContext.BaseDirectory, "runtime", "wintun.dll")))
            throw new FileNotFoundException("فایل رسمی Wintun کنار هسته پیدا نشد.");

        var configPath = Path.Combine(ProfileStore.Instance.ConfigDirectory, "active.json");
        var logPath = Path.Combine(ProfileStore.Instance.LogDirectory, "sing-box.log");
        var launched = false;
        var tunnelMode = settings.ConnectionMode == "TUN";
        var proxyPort = tunnelMode ? 2080 : ReserveLocalPort();
        _trafficApiPort = ReserveLocalPort();
        _trafficApiSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Interlocked.Exchange(ref _uploadBytes, 0);
        Interlocked.Exchange(ref _downloadBytes, 0);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tunnelMode && FindTunInterface() is not null)
                throw new InvalidOperationException("رابط MoghimiVPN از اجرای قبلی هنوز در ویندوز دیده می‌شود. سیستم را یک‌بار Restart کن و دوباره تلاش کن.");

            var json = SingBoxConfigBuilder.Build(profile, settings, logPath, proxyPort, _trafficApiPort, _trafficApiSecret);
            await File.WriteAllTextAsync(configPath, json, cancellationToken);
            await ValidateConfigAsync(configPath, cancellationToken);

            var hostPath = _tunnelHostPath;
            var startInfo = new ProcessStartInfo
            {
                // The host watches the desktop parent in both modes so an app crash cannot
                // leave an orphaned sing-box process or a stale local proxy listener.
                FileName = hostPath,
                Arguments = $"\"{_enginePath}\" \"{configPath}\" {Environment.ProcessId}",
                WorkingDirectory = Path.GetDirectoryName(_enginePath)!,
                CreateNoWindow = false,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            // Only system-wide TUN needs elevation. Proxy Mode remains a normal user process.
            if (tunnelMode) startInfo.Verb = "runas";

            try
            {
                _process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("راه‌اندازی هسته VPN ناموفق بود.");
                launched = true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException("درخواست دسترسی مدیر در پنجره UAC لغو شد؛ اتصال شروع نشد.", ex, cancellationToken);
            }

            var startedProcess = _process;
            startedProcess.EnableRaisingEvents = true;
            startedProcess.Exited += (_, _) =>
            {
                var unexpectedExit = ReferenceEquals(_process, startedProcess);
                if (unexpectedExit)
                {
                    _process = null;
                    try { startedProcess.Dispose(); } catch { }
                }
                _trafficMonitorCts?.Cancel();
                if (_systemProxyEnabled) SystemProxyController.Restore();
                _systemProxyEnabled = false;
                try { File.Delete(configPath); } catch { }

                // StopAsync detaches _process before intentionally closing the host.
                if (unexpectedExit)
                    EngineExited?.Invoke(this, EventArgs.Empty);
            };

            if (tunnelMode)
            {
                await WaitForTunInterfaceAsync(startedProcess, TimeSpan.FromSeconds(12), cancellationToken);
            }
            else
            {
                await WaitForProxyListenerAsync(startedProcess, proxyPort, TimeSpan.FromSeconds(8), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _systemProxyPort = proxyPort;
                SystemProxyController.Enable(port: proxyPort);
                _systemProxyEnabled = true;
                if (_HasExitedSafely(process))
                {
                    SystemProxyController.Restore();
                    _systemProxyEnabled = false;
                    throw new InvalidOperationException("هسته sing-box بلافاصله پس از فعال‌سازی پراکسی متوقف شد؛ تنظیمات قبلی ویندوز بازگردانده شد.");
                }
            }

            if (_HasExitedSafely(process))
                throw new InvalidOperationException("هسته VPN پیش از تکمیل راه‌اندازی متوقف شد.");

            _trafficMonitorCts?.Dispose();
            _trafficMonitorCts = new CancellationTokenSource();
            var monitorToken = _trafficMonitorCts.Token;
            _trafficMonitorTask = Task.Run(() => MonitorTrafficAsync(monitorToken), monitorToken);
            LogMessage?.Invoke(this, $"هسته VPN اجرا شد: {profile.Name}");
        }
        catch
        {
            if (launched && _process is not null)
                await StopAsync();
            else
            {
                try { File.Delete(configPath); } catch { }
            }
            throw;
        }
    }

    private static bool HasExitedSafely(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static int ReserveLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForProxyListenerAsync(
        Process process,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExitedSafely(process))
                throw new InvalidOperationException("sing-box پیش از آماده‌شدن پراکسی متوقف شد. گزارش اتصال را بررسی کن.");

            try
            {
                using var client = new TcpClient();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(350);
                await client.ConnectAsync(IPAddress.Loopback, port, linked.Token);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await Task.Delay(120, cancellationToken);
            }
        }
        throw new TimeoutException("پراکسی محلی sing-box در زمان مجاز آماده نشد؛ تنظیمات Windows Proxy تغییر داده نشد.");
    }

    private static NetworkInterface? FindTunInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(item => string.Equals(item.Name, "MoghimiVPN", StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static async Task WaitForTunInterfaceAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExitedSafely(process))
                throw new InvalidOperationException("هستهٔ VPN پیش از ساخت رابط TUN متوقف شد. گزارش اتصال را بررسی کن.");

            var adapter = FindTunInterface();
            if (adapter?.OperationalStatus == OperationalStatus.Up)
                return;

            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("رابط شبکهٔ MoghimiVPN در ویندوز آماده نشد؛ اتصال فعال اعلام نشد.");
    }

    private async Task ValidateConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo {
            FileName = _enginePath,
            Arguments = $"check -c \"{configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(_enginePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(start) ?? throw new InvalidOperationException("بررسی پیکربندی شروع نشد.");
        var stdout = p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = p.StandardError.ReadToEndAsync(cancellationToken);
        await p.WaitForExitAsync(cancellationToken);
        var details = (await stdout) + Environment.NewLine + (await stderr);
        if (p.ExitCode != 0)
            throw new InvalidOperationException("پیکربندی اتصال توسط هسته پذیرفته نشد: " + details.Trim());
    }

    public async Task StopAsync()
    {
        _trafficMonitorCts?.Cancel();
        var trafficTask = _trafficMonitorTask;
        _trafficMonitorTask = null;
        _trafficMonitorCts?.Dispose();
        _trafficMonitorCts = null;
        if (trafficTask is not null)
        {
            try { await trafficTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
        }

        var process = _process;
        _process = null;
        if (_systemProxyEnabled) SystemProxyController.Restore();
        _systemProxyEnabled = false;
        if (process is null) return;
        try
        {
            if (!HasExitedSafely(process))
            {
                // sing-box must close cleanly so it can remove its TUN interface and routes.
                if (OperatingSystem.IsWindows() && AttachConsole((uint)process.Id))
                {
                    try
                    {
                        SetConsoleCtrlHandler(IntPtr.Zero, true);
                        GenerateConsoleCtrlEvent(0, 0); // CTRL_C_EVENT to the attached console
                    }
                    finally
                    {
                        await Task.Delay(350);
                        FreeConsole();
                        SetConsoleCtrlHandler(IntPtr.Zero, false);
                    }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6)); } catch { }
                }
                if (!HasExitedSafely(process))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        catch { }
        finally
        {
            process.Dispose();
            try { File.Delete(Path.Combine(ProfileStore.Instance.ConfigDirectory, "active.json")); } catch { }
        }
        LogMessage?.Invoke(this, "اتصال متوقف شد.");
    }

    private async Task MonitorTrafficAsync(CancellationToken cancellationToken)
    {
        if (_trafficApiPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(_trafficApiSecret))
            return;

        var endpoint = new Uri($"http://127.0.0.1:{_trafficApiPort}/traffic");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _trafficApiSecret);
                using var response = await TrafficClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var upload = root.TryGetProperty("up", out var up) && up.TryGetInt64(out var upValue)
                        ? Math.Max(0, upValue) : 0;
                    var download = root.TryGetProperty("down", out var down) && down.TryGetInt64(out var downValue)
                        ? Math.Max(0, downValue) : 0;
                    if (upload > 0) Interlocked.Add(ref _uploadBytes, upload);
                    if (download > 0) Interlocked.Add(ref _downloadBytes, download);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                LogMessage?.Invoke(this, "Traffic monitor retry: " + error.GetType().Name);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(1000, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public static async Task<int> PingAsync(string host, int port, int timeoutMs = 2500)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535) return -1;
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return -1; }
    }

    public static string ReadLogTail(int maxLines = 70)
    {
        var path = Path.Combine(ProfileStore.Instance.LogDirectory, "sing-box.log");
        if (!File.Exists(path)) return "هنوز گزارشی ثبت نشده است.";
        try { return string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(maxLines)); }
        catch { return "خواندن گزارش ممکن نشد."; }
    }

    public void Dispose()
    {
        if (_systemProxyEnabled) SystemProxyController.Restore();
        // Normal window shutdown uses StopAsync before Dispose; this is a last-resort cleanup.
        try { if (_process is { } process && !HasExitedSafely(process)) process.Kill(true); } catch { }
        _process?.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
