using System.Diagnostics;
using System.Text;
using MoghimiVPN;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: MoghimiVPN.ConfigSmoke <path-to-sing-box.exe>");
    return 2;
}

var engine = Path.GetFullPath(args[0]);
if (!File.Exists(engine))
{
    Console.Error.WriteLine($"sing-box executable not found: {engine}");
    return 3;
}

var sampleKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
var baseSettings = new AppSettings
{
    DnsServer = "1.1.1.1",
    BlockAds = true,
    BypassLan = true
};

var profiles = new List<ProxyProfile>
{
    new() { Name = "VLESS TLS WS", Protocol = "vless", Server = "192.0.2.10", Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000001", Security = "tls", Sni = "example.com",
        Network = "ws", Path = "/vless", Host = "example.com", Fingerprint = "chrome" },
    new() { Name = "VLESS Reality", Protocol = "vless", Server = "192.0.2.11", Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000002", Security = "reality", Sni = "example.com",
        PublicKey = sampleKey, ShortId = "01234567", Network = "tcp", Fingerprint = "chrome" },
    new() { Name = "VMess TLS gRPC", Protocol = "vmess", Server = "192.0.2.12", Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000003", Security = "tls", Sni = "example.com",
        Network = "grpc", ServiceName = "vmess-service", Encryption = "auto" },
    new() { Name = "Trojan", Protocol = "trojan", Server = "192.0.2.13", Port = 443,
        Password = "sample-password", Security = "tls", Sni = "example.com" },
    new() { Name = "Shadowsocks", Protocol = "shadowsocks", Server = "192.0.2.14", Port = 8388,
        Method = "chacha20-ietf-poly1305", Password = "sample-password" },
    new() { Name = "Hysteria2", Protocol = "hysteria2", Server = "192.0.2.15", Port = 443,
        Password = "sample-password", Security = "tls", Sni = "example.com", ObfsType = " salamander".Trim(),
        ObfsPassword = "sample-obfs" },
    new() { Name = "TUIC", Protocol = "tuic", Server = "192.0.2.16", Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000004", Password = "sample-password",
        Security = "tls", Sni = "example.com" },
    new() { Name = "AnyTLS", Protocol = "anytls", Server = "192.0.2.17", Port = 443,
        Password = "sample-password", Security = "tls", Sni = "example.com" },
    new() { Name = "SOCKS5", Protocol = "socks", Server = "192.0.2.18", Port = 1080 },
    new() { Name = "HTTP", Protocol = "http", Server = "192.0.2.19", Port = 8080 },
    new() { Name = "WireGuard endpoint", Protocol = "wireguard", Server = "192.0.2.20", Port = 51820,
        PrivateKey = sampleKey, PeerPublicKey = sampleKey, LocalAddress = "10.0.0.2/32",
        AllowedIps = "0.0.0.0/0,::/0", Mtu = 1408 }
};

var tempRoot = Path.Combine(Path.GetTempPath(), "MoghimiVPN-ConfigSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
var failures = new List<string>();

try
{
    foreach (var profile in profiles)
    {
        foreach (var mode in new[] { "TUN", "System Proxy" })
        {
            var settings = new AppSettings
            {
                ConnectionMode = mode,
                DnsServer = baseSettings.DnsServer,
                BlockAds = baseSettings.BlockAds,
                BypassLan = baseSettings.BypassLan
            };
            var configPath = Path.Combine(tempRoot, $"{profile.Protocol}-{mode.Replace(" ", "-")}.json");
            try
            {
                var json = SingBoxConfigBuilder.Build(profile, settings, Path.Combine(tempRoot, "smoke.log"));
                await File.WriteAllTextAsync(configPath, json, Encoding.UTF8);

                var start = new ProcessStartInfo
                {
                    FileName = engine,
                    Arguments = $"check -c \"{configPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(start)
                    ?? throw new InvalidOperationException("Could not start sing-box check.");
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var output = (await stdoutTask) + Environment.NewLine + (await stderrTask);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(output.Trim());

                Console.WriteLine($"PASS  {profile.Protocol,-12}  {mode}");
                if (mode == "TUN")
                {
                    var probePath = Path.Combine(tempRoot, $"{profile.Protocol}-probe.json");
                    var probeJson = SingBoxConfigBuilder.BuildProbe(profile, 41873);
                    await File.WriteAllTextAsync(probePath, probeJson, Encoding.UTF8);
                    var probeStart = new ProcessStartInfo
                    {
                        FileName = engine,
                        Arguments = $"check -c \"{probePath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var probeProcess = Process.Start(probeStart)
                        ?? throw new InvalidOperationException("Could not start sing-box probe-config check.");
                    var probeOut = probeProcess.StandardOutput.ReadToEndAsync();
                    var probeErr = probeProcess.StandardError.ReadToEndAsync();
                    await probeProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    var probeDetails = (await probeOut) + Environment.NewLine + (await probeErr);
                    if (probeProcess.ExitCode != 0)
                        throw new InvalidOperationException("Latency probe config: " + probeDetails.Trim());
                    Console.WriteLine($"PASS  {profile.Protocol,-12}  latency probe");
                }
            }
            catch (Exception error)
            {
                failures.Add($"{profile.Protocol} / {mode}: {error.Message}");
                Console.Error.WriteLine($"FAIL  {profile.Protocol,-12}  {mode}: {error.Message}");
            }
        }
    }
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { }
}

Console.WriteLine($"Validated protocol, mode and probe configurations; failures: {failures.Count}.");
if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}
return 0;
