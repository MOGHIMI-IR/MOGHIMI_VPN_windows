using System.Windows;
using System.Windows.Controls;

namespace MoghimiVPN;

public partial class ProfileEditWindow : Window
{
    private readonly ProxyProfile _original;
    public ProxyProfile EditedProfile { get; private set; }

    public ProfileEditWindow(ProxyProfile? profile = null)
    {
        InitializeComponent();
        _original = profile is null ? new ProxyProfile() : Clone(profile);
        EditedProfile = Clone(_original);
        LoadValues();
    }

    private static ProxyProfile Clone(ProxyProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Protocol = p.Protocol,
        Server = p.Server,
        Port = p.Port,
        Uuid = p.Uuid,
        Password = p.Password,
        Method = p.Method,
        Flow = p.Flow,
        Network = p.Network,
        Path = p.Path,
        Host = p.Host,
        ServiceName = p.ServiceName,
        Security = p.Security,
        Sni = p.Sni,
        Fingerprint = p.Fingerprint,
        PublicKey = p.PublicKey,
        ShortId = p.ShortId,
        Alpn = p.Alpn,
        AllowInsecure = p.AllowInsecure,
        Encryption = p.Encryption,
        ObfsType = p.ObfsType,
        ObfsPassword = p.ObfsPassword,
        PrivateKey = p.PrivateKey,
        PeerPublicKey = p.PeerPublicKey,
        PreSharedKey = p.PreSharedKey,
        LocalAddress = p.LocalAddress,
        AllowedIps = p.AllowedIps,
        Mtu = p.Mtu,
        SubscriptionId = p.SubscriptionId,
        LatencyMs = p.LatencyMs
    };

    private void LoadValues()
    {
        NameBox.Text = _original.Name;
        SelectByTag(ProtocolCombo, _original.Protocol);
        ServerBox.Text = _original.Server;
        PortBox.Text = _original.Port.ToString();
        UuidBox.Text = _original.Uuid;
        PasswordTextBox.Text = _original.Password;
        MethodBox.Text = _original.Method;
        EncryptionBox.Text = _original.Encryption;
        SelectByTag(SecurityCombo, _original.Security);
        SelectByTag(NetworkCombo, _original.Network);
        SniBox.Text = _original.Sni;
        FingerprintBox.Text = _original.Fingerprint;
        FlowBox.Text = _original.Flow;
        AlpnBox.Text = _original.Alpn;
        PathBox.Text = _original.Path;
        HostBox.Text = _original.Host;
        ServiceNameBox.Text = _original.ServiceName;
        PublicKeyBox.Text = _original.PublicKey;
        ShortIdBox.Text = _original.ShortId;
        ObfsTypeBox.Text = _original.ObfsType;
        ObfsPasswordBox.Text = _original.ObfsPassword;
        AllowInsecureCheck.IsChecked = _original.AllowInsecure;
        PrivateKeyBox.Text = _original.PrivateKey;
        PeerPublicKeyBox.Text = _original.PeerPublicKey;
        PreSharedKeyBox.Text = _original.PreSharedKey;
        LocalAddressBox.Text = _original.LocalAddress;
        AllowedIpsBox.Text = _original.AllowedIps;
        MtuBox.Text = _original.Mtu.ToString();
        UpdateProtocolFields();
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == tag);
    }

    private static string SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

    private void ProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateProtocolFields();

    private void UpdateProtocolFields()
    {
        if (WireGuardPanel is null) return;
        WireGuardPanel.Visibility = SelectedTag(ProtocolCombo) == "wireguard"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(server) || Uri.CheckHostName(server) == UriHostNameType.Unknown)
        {
            MessageBox.Show("آدرس سرور معتبر نیست.", "کانفیگ نامعتبر", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("پورت باید عددی بین 1 تا 65535 باشد.", "کانفیگ نامعتبر",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var protocol = SelectedTag(ProtocolCombo);
        var uuid = UuidBox.Text.Trim();
        var password = PasswordTextBox.Text;
        if ((protocol is "vless" or "vmess") && string.IsNullOrWhiteSpace(uuid))
        {
            MessageBox.Show("برای این پروتکل UUID لازم است.", "کانفیگ نامعتبر",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if ((protocol is "trojan" or "hysteria2" or "anytls") && string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("برای این پروتکل Password لازم است.", "کانفیگ نامعتبر",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (protocol == "tuic" && (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(password)))
        {
            MessageBox.Show("برای TUIC هم UUID و هم Password لازم است.", "کانفیگ نامعتبر",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (protocol == "shadowsocks" &&
            (string.IsNullOrWhiteSpace(MethodBox.Text) || string.IsNullOrWhiteSpace(password)))
        {
            MessageBox.Show("برای Shadowsocks روش رمزنگاری و Password لازم است.", "کانفیگ نامعتبر",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (protocol == "wireguard" &&
            (string.IsNullOrWhiteSpace(PrivateKeyBox.Text) || string.IsNullOrWhiteSpace(PeerPublicKeyBox.Text)))
        {
            MessageBox.Show("برای WireGuard کلید خصوصی و کلید عمومی Peer لازم است.", "کانفیگ نامعتبر",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(MtuBox.Text.Trim(), out var mtu)) mtu = 1408;

        EditedProfile = Clone(_original);
        EditedProfile.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? $"{protocol.ToUpperInvariant()} · {server}" : NameBox.Text.Trim();
        EditedProfile.Protocol = protocol;
        EditedProfile.Server = server;
        EditedProfile.Port = port;
        EditedProfile.Uuid = uuid;
        EditedProfile.Password = password;
        EditedProfile.Method = string.IsNullOrWhiteSpace(MethodBox.Text) ? "chacha20-ietf-poly1305" : MethodBox.Text.Trim();
        EditedProfile.Encryption = string.IsNullOrWhiteSpace(EncryptionBox.Text) ? "auto" : EncryptionBox.Text.Trim();
        EditedProfile.Security = SelectedTag(SecurityCombo);
        EditedProfile.Network = SelectedTag(NetworkCombo);
        EditedProfile.Sni = SniBox.Text.Trim();
        EditedProfile.Fingerprint = FingerprintBox.Text.Trim();
        EditedProfile.Flow = FlowBox.Text.Trim();
        EditedProfile.Alpn = AlpnBox.Text.Trim();
        EditedProfile.Path = string.IsNullOrWhiteSpace(PathBox.Text) ? "/" : PathBox.Text.Trim();
        EditedProfile.Host = HostBox.Text.Trim();
        EditedProfile.ServiceName = ServiceNameBox.Text.Trim();
        EditedProfile.PublicKey = PublicKeyBox.Text.Trim();
        EditedProfile.ShortId = ShortIdBox.Text.Trim();
        EditedProfile.ObfsType = ObfsTypeBox.Text.Trim();
        EditedProfile.ObfsPassword = ObfsPasswordBox.Text;
        EditedProfile.AllowInsecure = AllowInsecureCheck.IsChecked == true;
        EditedProfile.PrivateKey = PrivateKeyBox.Text.Trim();
        EditedProfile.PeerPublicKey = PeerPublicKeyBox.Text.Trim();
        EditedProfile.PreSharedKey = PreSharedKeyBox.Text.Trim();
        EditedProfile.LocalAddress = string.IsNullOrWhiteSpace(LocalAddressBox.Text) ? "10.0.0.2/32" : LocalAddressBox.Text.Trim();
        EditedProfile.AllowedIps = string.IsNullOrWhiteSpace(AllowedIpsBox.Text) ? "0.0.0.0/0,::/0" : AllowedIpsBox.Text.Trim();
        EditedProfile.Mtu = mtu;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
