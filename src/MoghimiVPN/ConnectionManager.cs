using System.Diagnostics;
using System.Net.Sockets;

namespace MoghimiVPN;

public sealed class ConnectionManager : IDisposable
{
    private Process? _process;
    private bool _systemProxyEnabled;
    private readonly string _enginePath = Path.Combine(AppContext.BaseDirectory, "runtime", "sing-box.exe");
    public event EventHandler<string>? LogMessage;
    public event EventHandler? EngineExited;
    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(ProxyProfile profile, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("VPN از قبل در حال اجراست.");
        if (!File.Exists(_enginePath))
            throw new FileNotFoundException("هسته sing-box پیدا نشد. بسته نصب را کامل نصب یا دوباره دریافت کن.", _enginePath);
        if (settings.ConnectionMode == "TUN" && !File.Exists(Path.Combine(AppContext.BaseDirectory, "runtime", "wintun.dll")))
            throw new FileNotFoundException("فایل رسمی Wintun کنار هسته پیدا نشد.");

        var logPath = Path.Combine(ProfileStore.Instance.LogDirectory, "sing-box.log");
        var configPath = Path.Combine(ProfileStore.Instance.ConfigDirectory, "active.json");
        var json = SingBoxConfigBuilder.Build(profile, settings, logPath);
        await File.WriteAllTextAsync(configPath, json, cancellationToken);
        await ValidateConfigAsync(configPath, cancellationToken);

        var startInfo = new ProcessStartInfo {
            FileName = _enginePath,
            Arguments = $"run -c \"{configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(_enginePath)!,
            CreateNoWindow = true,
            UseShellExecute = settings.ConnectionMode == "TUN"
        };
        if (settings.ConnectionMode == "TUN")
        {
            startInfo.Verb = "runas";
        }
        else
        {
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        }

        try
        {
            _process = Process.Start(startInfo) ?? throw new InvalidOperationException("راه‌اندازی هسته VPN ناموفق بود.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("درخواست دسترسی مدیر در پنجره UAC لغو شد؛ اتصال شروع نشد.");
        }

        if (settings.ConnectionMode == "System Proxy")
        {
            try
            {
                await Task.Delay(1200, cancellationToken);
                if (_process.HasExited) throw new InvalidOperationException("هسته VPN پیش از آماده‌شدن متوقف شد. گزارش اتصال را بررسی کن.");
                SystemProxyController.Enable();
                _systemProxyEnabled = true;
            }
            catch
            {
                await StopAsync();
                throw;
            }
        }
        else
        {
            await Task.Delay(1800, cancellationToken);
            if (_process.HasExited)
                throw new InvalidOperationException("هسته VPN اجرا نشد. گزارش اتصال را بررسی کن.");
        }

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            if (_systemProxyEnabled) SystemProxyController.Restore();
            _systemProxyEnabled = false;
            try { File.Delete(Path.Combine(ProfileStore.Instance.ConfigDirectory, "active.json")); } catch { }
            EngineExited?.Invoke(this, EventArgs.Empty);
        };
        LogMessage?.Invoke(this, $"هسته VPN اجرا شد: {profile.Name}");
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
        var process = _process;
        _process = null;
        if (_systemProxyEnabled) SystemProxyController.Restore();
        _systemProxyEnabled = false;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
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
                if (!process.HasExited)
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
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
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
