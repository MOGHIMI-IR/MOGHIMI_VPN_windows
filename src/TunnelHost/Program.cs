using System.Diagnostics;

namespace MoghimiVPN.TunnelHost;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
            return 2;

        var enginePath = Path.GetFullPath(args[0]);
        var configPath = Path.GetFullPath(args[1]);
        if (!int.TryParse(args[2], out var parentPid) || parentPid <= 0)
            return 2;
        if (!File.Exists(enginePath) || !File.Exists(configPath))
            return 3;

        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
            if (parent.HasExited)
            {
                parent.Dispose();
                return 4;
            }
        }
        catch
        {
            return 4;
        }

        using (parent)
        using (var stop = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                try { stop.Cancel(); } catch { }
            };

            var startInfo = new ProcessStartInfo
            {
                FileName = enginePath,
                Arguments = $"run -c \"{configPath}\"",
                WorkingDirectory = Path.GetDirectoryName(enginePath)!,
                UseShellExecute = false,
                CreateNoWindow = false
            };

            using var engine = new Process { StartInfo = startInfo };
            try
            {
                if (!engine.Start())
                    return 5;

                var engineExit = engine.WaitForExitAsync();
                var parentExit = parent.WaitForExitAsync();
                var stopRequested = Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);

                var first = await Task.WhenAny(engineExit, parentExit, stopRequested);
                if (first == engineExit)
                {
                    await engineExit;
                    return engine.ExitCode;
                }

                if (first == parentExit)
                {
                    // The desktop UI died unexpectedly. Do not leave a detached VPN tunnel behind.
                    try { await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch
                    {
                        try { engine.Kill(entireProcessTree: true); } catch { }
                        try { await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
                    }
                }
                else
                {
                    // A normal disconnect should let sing-box remove its TUN adapter and routes.
                    try { await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
                    catch
                    {
                        try { engine.Kill(entireProcessTree: true); } catch { }
                        try { await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                    }
                }

                return engine.HasExited ? engine.ExitCode : 0;
            }
            catch
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
                return 6;
            }
        }
    }
}
