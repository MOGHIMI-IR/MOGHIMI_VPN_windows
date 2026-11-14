using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MoghimiVPN;

public sealed class ProxyProfile : INotifyPropertyChanged
{
    private string _name = "سرور جدید";
    private int _latencyMs = -1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Protocol { get; set; } = "vless";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 443;
    public string Uuid { get; set; } = "";
    public string Password { get; set; } = "";
    public string Method { get; set; } = "chacha20-ietf-poly1305";
    public string Flow { get; set; } = "";
    public string Network { get; set; } = "tcp";
    public string Path { get; set; } = "/";
    public string Host { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string Security { get; set; } = "tls";
    public string Sni { get; set; } = "";
    public string Fingerprint { get; set; } = "chrome";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string Alpn { get; set; } = "";
    public bool AllowInsecure { get; set; }
    public string Encryption { get; set; } = "auto";
    public string ObfsType { get; set; } = "";
    public string ObfsPassword { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string PeerPublicKey { get; set; } = "";
    public string PreSharedKey { get; set; } = "";
    public string LocalAddress { get; set; } = "10.0.0.2/32";
    public string AllowedIps { get; set; } = "0.0.0.0/0,::/0";
    public int Mtu { get; set; } = 1280;
    public string SubscriptionId { get; set; } = "";
    public int LatencyMs { get => _latencyMs; set { _latencyMs = value; OnPropertyChanged(); } }
    public string Subtitle => $"{Protocol.ToUpperInvariant()}  ·  {Server}:{Port}";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class SubscriptionItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "اشتراک جدید";
    public string Url { get; set; } = "";
    public DateTime LastUpdated { get; set; }
    public int ProfileCount { get; set; }
    public string LastStatus { get; set; } = "هنوز به‌روزرسانی نشده";
}

public sealed class AppSettings
{
    public string ConnectionMode { get; set; } = "TUN";
    public string DnsServer { get; set; } = "1.1.1.1";
    public bool BlockAds { get; set; } = true;
    public bool BypassLan { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutoConnect { get; set; }
    public string Theme { get; set; } = "dark-red";
}

public sealed class PersistedState
{
    public List<ProxyProfile> Profiles { get; set; } = [];
    public List<SubscriptionItem> Subscriptions { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
    public string? ActiveProfileId { get; set; }
}
