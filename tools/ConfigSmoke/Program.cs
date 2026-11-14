using System.Diagnostics;
using System.Net;
using System.Net.Http;
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
    new() { Name = "VLESS TLS WS", Protocol = "vless", Server = "vless.example.com", Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000001", Security = "tls", Sni = "example.com",
        Network = "ws", Path = "/vless", Host = "example.com", Fingerprint = "chrome" },
    new() { Name = "VLESS Reality", Protocol = "vless", Server = "192.0.2.11", Port = 443,
        Uuid = "00000000-0000-0000-0000-000000000002", Security = "reality", Sni = "example.com",
        PublicKey = sampleKey.TrimEnd('='), ShortId = "01234567", Network = "tcp", Fingerprint = "chrome" },
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

var failures = new List<string>();
var subscriptionPassed = false;
try
{
    var subscription = new SubscriptionItem
    {
        Id = "smoke-subscription",
        Name = "CI subscription",
        Url = "https://subscription.example.test/config"
    };
    using var http = new HttpClient(new SmokeResponseHandler(ProfileCodec.Encode(profiles[0])));
    var fetched = await new SubscriptionService(http).FetchAsync(subscription);
    subscriptionPassed = fetched.Count == 1 &&
                         fetched[0].SubscriptionId == subscription.Id &&
                         fetched[0].Protocol == profiles[0].Protocol;
    if (!subscriptionPassed) throw new InvalidOperationException("Subscription parser did not preserve the profile.");
    Console.WriteLine("PASS  subscription  bounded HTTPS fetch + profile association");
}
catch (Exception error)
{
    failures.Add("subscription: " + error.Message);
    Console.Error.WriteLine("FAIL  subscription: " + error.Message);
}

var downgradeBlocked = false;
try
{
    var subscription = new SubscriptionItem
    {
        Id = "downgrade-subscription",
        Name = "HTTPS downgrade test",
        Url = "https://subscription.example.test/config"
    };
    using var http = new HttpClient(new SmokeResponseHandler(
        ProfileCodec.Encode(profiles[0]),
        finalUri: new Uri("http://subscription.example.test/config")));
    await new SubscriptionService(http).FetchAsync(subscription);
}
catch (InvalidOperationException error) when (error.Message.Contains("به HTTP ناامن", StringComparison.Ordinal))
{
    downgradeBlocked = true;
}
if (downgradeBlocked)
    Console.WriteLine("PASS  subscription  HTTPS downgrade blocked");
else
{
    failures.Add("subscription downgrade: an HTTPS-to-HTTP redirect was accepted.");
    Console.Error.WriteLine("FAIL  subscription downgrade: an HTTPS-to-HTTP redirect was accepted.");
}

var oversizedBlocked = false;
try
{
    var subscription = new SubscriptionItem
    {
        Id = "oversize-subscription",
        Name = "Oversize response test",
        Url = "https://subscription.example.test/large"
    };
    using var http = new HttpClient(new SmokeResponseHandler(new string('A', 4 * 1024 * 1024 + 1)));
    await new SubscriptionService(http).FetchAsync(subscription);
}
catch (InvalidOperationException error) when (error.Message.Contains("4 مگابایت", StringComparison.Ordinal))
{
    oversizedBlocked = true;
}
if (oversizedBlocked)
    Console.WriteLine("PASS  subscription  response size limit enforced");
else
{
    failures.Add("subscription size: oversized response was accepted.");
    Console.Error.WriteLine("FAIL  subscription size: oversized response was accepted.");
}

var tempRoot = Path.Combine(Path.GetTempPath(), "MoghimiVPN-ConfigSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);

try
{
    foreach (var profile in profiles)
    {
        try
        {
            var encoded = ProfileCodec.Encode(profile);
            var decoded = ProfileCodec.DecodeMany(encoded);
            if (decoded.Count != 1 ||
                !string.Equals(decoded[0].Protocol, profile.Protocol, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(decoded[0].Server, profile.Server, StringComparison.OrdinalIgnoreCase) ||
                decoded[0].Port != profile.Port)
                throw new InvalidOperationException("Profile link encode/decode round-trip did not preserve protocol/server/port.");

            var qrBytes = QrCodeService.CreatePng(encoded);
            var qrPath = Path.Combine(tempRoot, $"qr-{profile.Protocol}.png");
            await File.WriteAllBytesAsync(qrPath, qrBytes);
            var qrText = QrCodeService.DecodeFromImage(qrPath);
            var qrProfile = ProfileCodec.DecodeMany(qrText);
            if (qrProfile.Count != 1 || !string.Equals(qrProfile[0].Protocol, profile.Protocol, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("QR encode/decode round-trip did not preserve profile protocol.");

            Console.WriteLine($"PASS  {profile.Protocol,-12}  link + QR round-trip");
        }
        catch (Exception error)
        {
            failures.Add($"{profile.Protocol} / link+QR: {error.Message}");
            Console.Error.WriteLine($"FAIL  {profile.Protocol,-12}  link+QR: {error.Message}");
        }

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
                await File.WriteAllTextAsync(configPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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
                    await File.WriteAllTextAsync(probePath, probeJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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


internal sealed class SmokeResponseHandler : HttpMessageHandler
{
    private readonly string _body;
    private readonly Uri? _finalUri;

    public SmokeResponseHandler(string body, Uri? finalUri = null)
    {
        _body = body;
        _finalUri = finalUri;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_body, Encoding.UTF8, "text/plain"),
            RequestMessage = new HttpRequestMessage(request.Method, _finalUri ?? request.RequestUri)
        };
        return Task.FromResult(response);
    }
}
