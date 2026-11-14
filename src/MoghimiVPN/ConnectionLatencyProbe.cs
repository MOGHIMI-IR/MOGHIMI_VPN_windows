using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MoghimiVPN;

public static class ConnectionLatencyProbe
{
    private static readonly string EnginePath =
        Path.Combine(AppContext.BaseDirectory, "runtime", "sing-box.exe");

    private static readonly Uri[] ProbeUris =
    [
        new("https://www.gstatic.com/generate_204"),
        new("https://cp.cloudflare.com/generate_204")
    ];

    public static async Task<int> TestAsync(
        ProxyProfile profile,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(EnginePath))
            throw new FileNotFoundException("هسته sing-box برای تست تأخیر پیدا نشد.", EnginePath);

        var port = ReserveLocalPort();
        var directory = ProfileStore.Instance.ConfigDirectory;
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, $"probe-{Guid.NewGuid():N}.json");

        Process? engine = null;
        try
        {
            var config = SingBoxConfigBuilder.BuildProbe(profile, port);
            await File.WriteAllTextAsync(configPath, config, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = EnginePath,
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(EnginePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            engine = Process.Start(startInfo)
                ?? throw new InvalidOperationException("شروع تست اتصال سرور ناموفق بود.");

            // Drain both streams so a verbose core cannot block on a full pipe.
            var stdoutTask = engine.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = engine.StandardError.ReadToEndAsync(cancellationToken);

            if (!await WaitForProxyAsync(engine, port, TimeSpan.FromSeconds(6), cancellationToken))
            {
                var error = engine.HasExited ? await SafeReadAsync(stderrTask) : "پراکسی آزمایشی آماده نشد.";
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "پراکسی آزمایشی آماده نشد." : error.Trim());
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
                AllowAutoRedirect = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(4)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MoghimiVPN-ConnectivityCheck/1.0");

            foreach (var uri in ProbeUris)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = await client.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if ((int)response.StatusCode is >= 200 and <= 299)
                        return Math.Max(1, (int)timer.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Try the second independent HTTPS probe endpoint.
                }
            }

            return -1;
        }
        finally
        {
            if (engine is not null)
            {
                try
                {
                    if (!engine.HasExited)
                    {
                        engine.Kill(entireProcessTree: true);
                        await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    }
                }
                catch { }
                engine.Dispose();
            }

            try { File.Delete(configPath); } catch { }
        }
    }

    private static int ReserveLocalPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> WaitForProxyAsync(
        Process process,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) return false;

            try
            {
                using var client = new TcpClient();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(350);
                await client.ConnectAsync(IPAddress.Loopback, port, linked.Token);
                return true;
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
        return false;
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask)
    {
        try { return await readTask.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch { return ""; }
    }
}
