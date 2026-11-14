using System.Diagnostics;

namespace MoghimiVPN.TunnelHost;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) return 2;

        var enginePath = Path.GetFullPath(args[0]);
        var configPath = Path.GetFullPath(args[1]);
        if (!File.Exists(enginePath) || !File.Exists(configPath)) return 3;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = enginePath,
            Arguments = $"run -c \\"{configPath}\\"",
            WorkingDirectory = Path.GetDirectoryName(enginePath)!,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        using var engine = new Process { StartInfo = startInfo };
        try
        {
            if (!engine.Start()) return 4;
            try
            {
                await engine.WaitForExitAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                // Let sing-box remove the TUN interface and routes before any forced cleanup.
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
            return 5;
        }
    }
}
