using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoghimiVPN;

public static class SingBoxConfigBuilder
{
    public static bool IsTransportSupported(string? network) =>
        (network ?? "tcp").Trim().ToLowerInvariant() is
            "" or "tcp" or "raw" or "none" or "ws" or "grpc" or "h2" or "http" or "httpupgrade" or "http-upgrade" or "quic";

    public static string Build(ProxyProfile p, AppSettings settings, string logFile, int mixedPort = 2080, int? trafficApiPort = null, string? trafficApiSecret = null)
    {
        if (string.IsNullOrWhiteSpace(p.Server) || p.Port is < 1 or > 65535)
            throw new InvalidOperationException("آدرس یا پورت سرور معتبر نیست.");

        var isWireGuard = p.Protocol == "wireguard";
        var proxy = isWireGuard
            ? new JsonObject { ["type"] = "direct", ["tag"] = "direct-via-wireguard" }
            : BuildOutbound(p);
        var endpoints = new JsonArray();
        if (isWireGuard) endpoints.Add(BuildWireGuardEndpoint(p));
        var blocked = new JsonArray(
            "doubleclick.net", "googlesyndication.com", "googleadservices.com",
            "adservice.google.com", "adnxs.com", "taboola.com", "outbrain.com",
            "adcolony.com", "app-measurement.com"
        );
        var rules = new JsonArray();
        rules.Add(new JsonObject { ["protocol"] = new JsonArray("dns"), ["action"] = "hijack-dns" });
        if (settings.BypassLan)
            rules.Add(new JsonObject { ["ip_is_private"] = true, ["action"] = "route", ["outbound"] = "direct" });
        if (settings.BlockAds)
            rules.Add(new JsonObject { ["domain_suffix"] = blocked, ["action"] = "reject" });

        var inbounds = new JsonArray();
        if (settings.ConnectionMode == "System Proxy")
        {
            inbounds.Add(new JsonObject {
                ["type"] = "mixed", ["tag"] = "mixed-in",
                ["listen"] = "127.0.0.1", ["listen_port"] = mixedPort
            });
        }
        else
        {
            inbounds.Add(new JsonObject {
                ["type"] = "tun", ["tag"] = "tun-in",
                ["interface_name"] = "MoghimiVPN",
                ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                ["mtu"] = 1400,
                ["stack"] = "mixed",
                ["dns_mode"] = "hijack",
                ["auto_route"] = true,
                ["strict_route"] = true
            });
        }

        var dnsInput = settings.DnsServer.Trim();
        if (string.IsNullOrWhiteSpace(dnsInput)) dnsInput = "1.1.1.1";

        var dnsType = "udp";
        var dnsServer = dnsInput;
        var dnsPort = 0;
        var dnsPath = "";
        if (Uri.TryCreate(dnsInput, UriKind.Absolute, out var dnsUri) &&
            dnsInput.Contains("://", StringComparison.Ordinal))
        {
            dnsType = dnsUri.Scheme.ToLowerInvariant() switch
            {
                "udp" => "udp",
                "tcp" => "tcp",
                "tls" or "dot" => "tls",
                "https" or "doh" => "https",
                _ => throw new InvalidOperationException("نوع DNS واردشده پشتیبانی نمی‌شود؛ از udp://، tcp://، tls:// یا https:// استفاده کن.")
            };
            dnsServer = dnsUri.Host;
            if (!dnsUri.IsDefaultPort) dnsPort = dnsUri.Port;
            if (dnsType == "https") dnsPath = string.IsNullOrWhiteSpace(dnsUri.AbsolutePath) ? "/dns-query" : dnsUri.AbsolutePath;
        }

        if (string.IsNullOrWhiteSpace(dnsServer))
            throw new InvalidOperationException("آدرس DNS معتبر نیست.");

        var dnsConfig = new JsonObject
        {
            ["type"] = dnsType,
            ["tag"] = "dns-remote",
            ["server"] = dnsServer,
            ["detour"] = "proxy"
        };
        if (dnsPort > 0) dnsConfig["server_port"] = dnsPort;
        // Resolve a domain used by the configured DNS server outside the tunnel as well.
        dnsConfig["domain_resolver"] = "dns-bootstrap";
        if (dnsType == "https")
        {
            dnsConfig["path"] = dnsPath;
            dnsConfig["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = dnsServer };
        }
        else if (dnsType == "tls")
        {
            dnsConfig["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = dnsServer };
        }
        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info", ["timestamp"] = true, ["output"] = logFile },
            ["dns"] = new JsonObject {
                ["servers"] = new JsonArray(
                    new JsonObject {
                        ["type"] = "udp",
                        ["tag"] = "dns-bootstrap",
                        ["server"] = "1.1.1.1",
                        ["detour"] = "direct"
                    },
                    dnsConfig
                ),
                ["final"] = "dns-remote"
            },
            ["inbounds"] = inbounds,
            ["endpoints"] = endpoints,
            ["outbounds"] = new JsonArray(
                proxy,
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "block", ["tag"] = "block" }
            ),
            ["route"] = new JsonObject {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "dns-bootstrap",
                ["final"] = "proxy",
                ["rules"] = rules
            }
        };
        if (trafficApiPort is > 0 && !string.IsNullOrWhiteSpace(trafficApiSecret))
        {
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{trafficApiPort.Value}",
                    ["secret"] = trafficApiSecret
                }
            };
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }


    public static string BuildProbe(ProxyProfile p, int listenPort)
    {
        if (listenPort is < 1024 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(listenPort));

        var isWireGuard = p.Protocol == "wireguard";
        var endpoints = new JsonArray();
        var proxy = isWireGuard
            ? new JsonObject { ["type"] = "direct", ["tag"] = "direct-via-wireguard" }
            : BuildOutbound(p);

        if (isWireGuard) endpoints.Add(BuildWireGuardEndpoint(p));

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "error", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject
                {
                    ["type"] = "udp",
                    ["tag"] = "probe-dns",
                    ["server"] = "1.1.1.1"
                }),
                ["final"] = "probe-dns"
            },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = "probe-in",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = listenPort
            }),
            ["endpoints"] = endpoints,
            ["outbounds"] = new JsonArray(
                proxy,
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
                new JsonObject { ["type"] = "block", ["tag"] = "block" }
            ),
            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "probe-dns",
                ["final"] = "proxy"
            }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonObject BuildWireGuardEndpoint(ProxyProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.PrivateKey) || string.IsNullOrWhiteSpace(p.PeerPublicKey))
            throw new InvalidOperationException("برای WireGuard کلید خصوصی و کلید عمومی Peer لازم است.");
        if (string.IsNullOrWhiteSpace(p.Server) || p.Port is < 1 or > 65535)
            throw new InvalidOperationException("آدرس یا پورت سرور WireGuard معتبر نیست.");

        var addresses = p.LocalAddress.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var allowed = p.AllowedIps.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (addresses.Length == 0 || allowed.Length == 0)
            throw new InvalidOperationException("آدرس داخلی و Allowed IPs در کانفیگ WireGuard کامل نیست.");

        var peer = new JsonObject
        {
            ["address"] = p.Server,
            ["port"] = p.Port,
            ["public_key"] = p.PeerPublicKey,
            ["allowed_ips"] = new JsonArray(allowed.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["persistent_keepalive_interval"] = 25,
            ["reserved"] = new JsonArray(0, 0, 0)
        };
        if (!string.IsNullOrWhiteSpace(p.PreSharedKey))
            peer["pre_shared_key"] = p.PreSharedKey;

        return new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = "proxy",
            ["system"] = false,
            ["mtu"] = p.Mtu is >= 576 and <= 9000 ? p.Mtu : 1408,
            ["address"] = new JsonArray(addresses.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["private_key"] = p.PrivateKey,
            ["peers"] = new JsonArray(peer)
        };
    }
    private static JsonObject BuildOutbound(ProxyProfile p)
    {
        var o = new JsonObject { ["tag"] = "proxy", ["server"] = p.Server, ["server_port"] = p.Port };
        switch (p.Protocol)
        {
            case "vless":
                o["type"] = "vless"; o["uuid"] = p.Uuid;
                if (!string.IsNullOrWhiteSpace(p.Flow)) o["flow"] = p.Flow;
                AddTlsAndTransport(o, p);
                break;
            case "vmess":
                o["type"] = "vmess"; o["uuid"] = p.Uuid;
                o["security"] = string.IsNullOrWhiteSpace(p.Encryption) ? "auto" : p.Encryption;
                o["alter_id"] = 0;
                AddTlsAndTransport(o, p);
                break;
            case "trojan":
                o["type"] = "trojan"; o["password"] = p.Password;
                AddTlsAndTransport(o, p);
                break;
            case "shadowsocks":
                o["type"] = "shadowsocks"; o["method"] = p.Method; o["password"] = p.Password;
                break;
            case "hysteria2":
                o["type"] = "hysteria2"; o["password"] = p.Password;
                o["tls"] = BuildTls(p, forceTls: true);
                if (!string.IsNullOrWhiteSpace(p.ObfsType))
                    o["obfs"] = new JsonObject { ["type"] = p.ObfsType, ["password"] = p.ObfsPassword };
                break;
            case "tuic":
                o["type"] = "tuic"; o["uuid"] = p.Uuid; o["password"] = p.Password;
                o["congestion_control"] = "bbr";
                o["udp_relay_mode"] = "native";
                o["tls"] = BuildTls(p, forceTls: true);
                break;
            case "anytls":
                o["type"] = "anytls"; o["password"] = string.IsNullOrWhiteSpace(p.Password) ? p.Uuid : p.Password;
                o["tls"] = BuildTls(p, forceTls: true);
                break;
            case "socks":
                o["type"] = "socks"; o["version"] = "5";
                if (!string.IsNullOrWhiteSpace(p.Uuid)) o["username"] = p.Uuid;
                if (!string.IsNullOrWhiteSpace(p.Password)) o["password"] = p.Password;
                break;
            case "http":
                o["type"] = "http";
                if (!string.IsNullOrWhiteSpace(p.Uuid)) o["username"] = p.Uuid;
                if (!string.IsNullOrWhiteSpace(p.Password)) o["password"] = p.Password;
                break;
            case "wireguard":
                throw new NotSupportedException("WireGuard باید از طریق WireGuard endpoint ساخته شود.");
            default:
                throw new NotSupportedException($"پروتکل {p.Protocol} در این نسخه پشتیبانی نشده است.");
        }
        return o;
    }

    private static void AddTlsAndTransport(JsonObject o, ProxyProfile p)
    {
        var security = p.Security.ToLowerInvariant();
        if ((security is "tls" or "reality" or "xtls") || (p.Protocol is "trojan" or "vmess"))
            o["tls"] = BuildTls(p, forceTls: p.Protocol is "trojan");
        var network = p.Network.ToLowerInvariant();
        switch (network)
        {
            case "tcp":
            case "raw":
            case "none":
            case "":
                break;
            case "ws":
                o["transport"] = new JsonObject {
                    ["type"] = "ws", ["path"] = string.IsNullOrWhiteSpace(p.Path) ? "/" : p.Path,
                    ["headers"] = string.IsNullOrWhiteSpace(p.Host) ? new JsonObject() : new JsonObject { ["Host"] = p.Host }
                };
                break;
            case "grpc":
                o["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = p.ServiceName.Length > 0 ? p.ServiceName : p.Path.Trim('/') };
                break;
            case "h2":
            case "http":
                o["transport"] = new JsonObject {
                    ["type"] = "http", ["host"] = string.IsNullOrWhiteSpace(p.Host) ? new JsonArray() : new JsonArray(p.Host),
                    ["path"] = string.IsNullOrWhiteSpace(p.Path) ? "/" : p.Path
                };
                break;
            case "httpupgrade":
            case "http-upgrade":
                o["transport"] = new JsonObject {
                    ["type"] = "httpupgrade", ["host"] = p.Host,
                    ["path"] = string.IsNullOrWhiteSpace(p.Path) ? "/" : p.Path
                };
                break;
            case "quic":
                o["transport"] = new JsonObject { ["type"] = "quic" };
                break;
            default:
                throw new NotSupportedException($"انتقال {p.Network} در sing-box این نسخه پشتیبانی نمی‌شود؛ mKCP، DomainSocket و XHTTP قابل استفاده نیستند.");
        }
    }

    private static JsonObject BuildTls(ProxyProfile p, bool forceTls = false)
    {
        var tls = new JsonObject {
            ["enabled"] = forceTls || p.Security is "tls" or "reality" or "xtls",
            ["server_name"] = string.IsNullOrWhiteSpace(p.Sni) ? p.Server : p.Sni,
            ["insecure"] = p.AllowInsecure
        };
        if (!string.IsNullOrWhiteSpace(p.Alpn))
            tls["alpn"] = new JsonArray(p.Alpn.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        if (!string.IsNullOrWhiteSpace(p.Fingerprint))
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = p.Fingerprint };
        if (p.Security == "reality")
        {
            if (string.IsNullOrWhiteSpace(p.PublicKey))
                throw new InvalidOperationException("کلید عمومی Reality (pbk) وارد نشده است.");
            tls["reality"] = new JsonObject {
                ["enabled"] = true,
                ["public_key"] = NormalizeRealityPublicKey(p.PublicKey),
                ["short_id"] = p.ShortId
            };
        }
        return tls;
    }

    private static string NormalizeRealityPublicKey(string input)
    {
        var value = input.Trim().Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
        byte[] decoded;
        try { decoded = Convert.FromBase64String(value); }
        catch (FormatException)
        {
            throw new InvalidOperationException("کلید عمومی Reality باید Base64 یا Base64URL معتبر باشد.");
        }

        if (decoded.Length != 32)
            throw new InvalidOperationException("کلید عمومی Reality باید دقیقاً 32 بایت باشد.");

        return Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
