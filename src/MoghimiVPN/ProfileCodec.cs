using System.Text;
using System.Text.Json;
using System.Web;

namespace MoghimiVPN;

public static class ProfileCodec
{
    public static ProxyProfile? Decode(string input)
    {
        var raw = input.Trim().Trim('\uFEFF');
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return null;
        var scheme = raw[..schemeEnd].ToLowerInvariant();
        if (scheme == "vmess") return DecodeVmess(raw);
        if (scheme == "ss") return DecodeShadowsocks(raw);

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        var q = HttpUtility.ParseQueryString(uri.Query);
        var profile = new ProxyProfile
        {
            Name = DecodeName(uri.Fragment),
            Protocol = scheme switch
            {
                "vless" => "vless",
                "trojan" => "trojan",
                "hysteria2" or "hy2" => "hysteria2",
                "tuic" => "tuic",
                "anytls" => "anytls",
                "socks" or "socks5" or "socks5h" => "socks",
                "http" or "https" => "http",
                "wireguard" or "wg" => "wireguard",
                _ => ""
            },
            Server = uri.Host,
            Port = uri.Port > 0 ? uri.Port : DefaultPort(scheme),
            Uuid = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]),
            Password = uri.UserInfo.Contains(':') ? Uri.UnescapeDataString(uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..]) : "",
            Network = q["type"] ?? q["network"] ?? "tcp",
            Path = q["path"] ?? q["serviceName"] ?? "/",
            Host = q["host"] ?? "",
            ServiceName = q["serviceName"] ?? "",
            Security = q["security"] ?? (scheme is "hysteria2" or "hy2" or "tuic" or "anytls" or "https" ? "tls" : "none"),
            Sni = q["sni"] ?? q["peer"] ?? "",
            Fingerprint = q["fp"] ?? "chrome",
            PublicKey = q["pbk"] ?? q["public_key"] ?? "",
            ShortId = q["sid"] ?? q["short_id"] ?? "",
            Flow = q["flow"] ?? "",
            Alpn = q["alpn"] ?? "",
            AllowInsecure = q["allowInsecure"] == "1" || q["insecure"] == "1",
            Method = q["method"] ?? "chacha20-ietf-poly1305",
            ObfsType = q["obfs"] ?? "",
            ObfsPassword = q["obfs-password"] ?? q["obfs_password"] ?? "",
            PrivateKey = q["private_key"] ?? q["privatekey"] ?? "",
            PeerPublicKey = q["public_key"] ?? q["publickey"] ?? "",
            PreSharedKey = q["preshared_key"] ?? q["pre_shared_key"] ?? "",
            LocalAddress = q["address"] ?? "10.0.0.2/32",
            AllowedIps = q["allowed_ips"] ?? "0.0.0.0/0,::/0"
        };

        if (profile.Protocol == "wireguard" && string.IsNullOrWhiteSpace(profile.PrivateKey))
            profile.PrivateKey = profile.Uuid;
        if (profile.Protocol == "shadowsocks" || string.IsNullOrWhiteSpace(profile.Protocol))
            return null;
        if (string.IsNullOrWhiteSpace(profile.Server) || profile.Port is < 1 or > 65535) return null;
        if ((profile.Protocol is "vless" or "vmess") && string.IsNullOrWhiteSpace(profile.Uuid)) return null;
        if ((profile.Protocol is "trojan" or "anytls" or "hysteria2") && string.IsNullOrWhiteSpace(profile.Password))
            profile.Password = profile.Uuid;
        if (profile.Protocol == "tuic" && string.IsNullOrWhiteSpace(profile.Password))
            profile.Password = q["password"] ?? "";
        if (profile.Protocol == "vless" && profile.Security == "reality" && string.IsNullOrWhiteSpace(profile.PublicKey))
            return null;
        return profile;
    }

    public static string Encode(ProxyProfile p)
    {
        var host = p.Server.Contains(':') && !p.Server.StartsWith("[", StringComparison.Ordinal)
            ? $"[{p.Server}]"
            : p.Server;
        var endpoint = $"{host}:{p.Port}";
        var label = Uri.EscapeDataString(string.IsNullOrWhiteSpace(p.Name) ? p.Protocol : p.Name);

        string Query(params (string Key, string? Value)[] fields) =>
            string.Join("&", fields
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));

        switch (p.Protocol)
        {
            case "vless":
                return $"vless://{Uri.EscapeDataString(p.Uuid)}@{endpoint}?{Query(
                    ("encryption", "none"), ("flow", p.Flow), ("security", p.Security),
                    ("sni", p.Sni), ("fp", p.Fingerprint), ("pbk", p.PublicKey),
                    ("sid", p.ShortId), ("alpn", p.Alpn), ("type", p.Network),
                    ("path", p.Path), ("host", p.Host),
                    ("serviceName", p.ServiceName), ("allowInsecure", p.AllowInsecure ? "1" : null)
                )}#{label}";
            case "vmess":
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    v = "2", ps = p.Name, add = p.Server, port = p.Port.ToString(),
                    id = p.Uuid, aid = "0", scy = p.Encryption, net = p.Network,
                    type = "none", host = p.Host, path = p.Path,
                    tls = p.Security == "tls" ? "tls" : "",
                    sni = p.Sni, fp = p.Fingerprint
                });
                return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            }
            case "trojan":
                return $"trojan://{Uri.EscapeDataString(p.Password)}@{endpoint}?{Query(
                    ("security", "tls"), ("sni", p.Sni), ("fp", p.Fingerprint),
                    ("alpn", p.Alpn), ("type", p.Network), ("path", p.Path),
                    ("host", p.Host), ("serviceName", p.ServiceName),
                    ("allowInsecure", p.AllowInsecure ? "1" : null)
                )}#{label}";
            case "shadowsocks":
            {
                var user = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{p.Method}:{p.Password}"));
                return $"ss://{user}@{endpoint}#{label}";
            }
            case "hysteria2":
            case "hy2":
                return $"hysteria2://{Uri.EscapeDataString(p.Password)}@{endpoint}?{Query(
                    ("security", "tls"), ("sni", p.Sni), ("alpn", p.Alpn),
                    ("obfs", p.ObfsType), ("obfs-password", p.ObfsPassword),
                    ("insecure", p.AllowInsecure ? "1" : null)
                )}#{label}";
            case "tuic":
                return $"tuic://{Uri.EscapeDataString(p.Uuid)}:{Uri.EscapeDataString(p.Password)}@{endpoint}?{Query(
                    ("security", "tls"), ("sni", p.Sni), ("alpn", p.Alpn),
                    ("congestion_control", "bbr"), ("udp_relay_mode", "native")
                )}#{label}";
            case "anytls":
                return $"anytls://{Uri.EscapeDataString(p.Password)}@{endpoint}?{Query(
                    ("security", "tls"), ("sni", p.Sni), ("alpn", p.Alpn),
                    ("insecure", p.AllowInsecure ? "1" : null)
                )}#{label}";
            case "socks":
                var socksUser = string.IsNullOrWhiteSpace(p.Uuid)
                    ? $":{Uri.EscapeDataString(p.Password)}"
                    : $"{Uri.EscapeDataString(p.Uuid)}:{Uri.EscapeDataString(p.Password)}";
                return $"socks5://{socksUser}@{endpoint}#{label}";
            case "http":
                var httpUser = string.IsNullOrWhiteSpace(p.Uuid)
                    ? $":{Uri.EscapeDataString(p.Password)}"
                    : $"{Uri.EscapeDataString(p.Uuid)}:{Uri.EscapeDataString(p.Password)}";
                return $"http://{httpUser}@{endpoint}#{label}";
            case "wireguard":
                return $"wireguard://{Uri.EscapeDataString(p.PrivateKey)}@{endpoint}?{Query(
                    ("public_key", p.PeerPublicKey), ("preshared_key", p.PreSharedKey),
                    ("address", p.LocalAddress), ("allowed_ips", p.AllowedIps), ("mtu", p.Mtu.ToString())
                )}#{label}";
            default:
                throw new NotSupportedException($"پروتکل {p.Protocol} قابل خروجی گرفتن نیست.");
        }
    }

    public static List<ProxyProfile> DecodeMany(string body)
    {
        var text = body.Trim().Trim('\uFEFF');
        if (string.IsNullOrWhiteSpace(text)) return [];
        var decoded = TryBase64(text);
        if (!string.IsNullOrWhiteSpace(decoded) &&
            (decoded.Contains("://", StringComparison.OrdinalIgnoreCase) ||
             decoded.Contains("vmess://", StringComparison.OrdinalIgnoreCase)))
            text = decoded;
        var result = new List<ProxyProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = Decode(line);
            if (p is null) continue;
            var key = $"{p.Protocol}|{p.Server}|{p.Port}|{p.Uuid}|{p.Password}";
            if (seen.Add(key)) result.Add(p);
        }
        return result;
    }

    private static ProxyProfile? DecodeVmess(string raw)
    {
        var payload = TryBase64(raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..]);
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            string Get(string key, string fallback = "") =>
                root.TryGetProperty(key, out var value) ? value.ToString() : fallback;
            if (!int.TryParse(Get("port"), out var port)) port = 443;
            return new ProxyProfile
            {
                Name = Get("ps", "VMess"),
                Protocol = "vmess",
                Server = Get("add"),
                Port = port,
                Uuid = Get("id"),
                Encryption = Get("scy", "auto"),
                Network = Get("net", "tcp"),
                Path = Get("path", "/"),
                Host = Get("host"),
                Security = string.IsNullOrWhiteSpace(Get("tls")) ? "none" : "tls",
                Sni = Get("sni"),
                AllowInsecure = Get("allowInsecure") == "1"
            };
        }
        catch { return null; }
    }

    private static ProxyProfile? DecodeShadowsocks(string raw)
    {
        try
        {
            var withoutScheme = raw[5..];
            var fragmentSplit = withoutScheme.Split('#', 2);
            var main = fragmentSplit[0];
            var name = fragmentSplit.Length > 1 ? Uri.UnescapeDataString(fragmentSplit[1]) : "Shadowsocks";
            string methodPassword;
            string hostPort;
            var at = main.LastIndexOf('@');
            if (at >= 0)
            {
                var user = main[..at];
                hostPort = main[(at + 1)..];
                methodPassword = user.Contains(':') ? user : TryBase64(user) ?? user;
            }
            else
            {
                var decoded = TryBase64(main) ?? main;
                var split = decoded.LastIndexOf('@');
                if (split < 0) return null;
                methodPassword = decoded[..split];
                hostPort = decoded[(split + 1)..];
            }
            var sep = methodPassword.IndexOf(':');
            if (sep < 1 || !Uri.TryCreate("ss://" + hostPort, UriKind.Absolute, out var uri)) return null;
            return new ProxyProfile
            {
                Name = name, Protocol = "shadowsocks", Server = uri.Host,
                Port = uri.IsDefaultPort ? 8388 : uri.Port,
                Method = methodPassword[..sep],
                Password = Uri.UnescapeDataString(methodPassword[(sep + 1)..])
            };
        }
        catch { return null; }
    }

    private static string? TryBase64(string value)
    {
        var candidate = value.Trim().Replace('-', '+').Replace('_', '/');
        candidate = candidate.PadRight(candidate.Length + ((4 - candidate.Length % 4) % 4), '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(candidate)); }
        catch { return null; }
    }

    private static string DecodeName(string fragment) =>
        string.IsNullOrWhiteSpace(fragment) ? "سرور جدید" : Uri.UnescapeDataString(fragment.TrimStart('#'));

    private static int DefaultPort(string scheme) => scheme switch
    {
        "socks" or "socks5" => 1080,
        "http" => 8080,
        "ss" => 8388,
        "hysteria2" or "hy2" or "tuic" => 443,
        _ => 443
    };
}
