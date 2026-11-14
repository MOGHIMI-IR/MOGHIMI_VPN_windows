using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace MoghimiVPN;

public static class SystemProxyController
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MoghimiVPN", "proxy-backup.dpapi");

    private sealed class Snapshot
    {
        public int? ProxyEnable { get; set; }
        public string? ProxyServer { get; set; }
        public string? ProxyOverride { get; set; }
        public string? AutoConfigURL { get; set; }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);

    public static void Enable(string host = "127.0.0.1", int port = 2080)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("تنظیمات پراکسی ویندوز در دسترس نیست.");
        if (!File.Exists(BackupPath))
        {
            var snapshot = new Snapshot {
                ProxyEnable = key.GetValue("ProxyEnable") is int enabled ? enabled : null,
                ProxyServer = key.GetValue("ProxyServer") as string,
                ProxyOverride = key.GetValue("ProxyOverride") as string,
                AutoConfigURL = key.GetValue("AutoConfigURL") as string
            };
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            var clear = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            var encrypted = ProtectedData.Protect(clear, ProfileStore.Entropy(), DataProtectionScope.CurrentUser);
            var temporary = BackupPath + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            if (File.Exists(BackupPath)) File.Replace(temporary, BackupPath, null);
            else File.Move(temporary, BackupPath);
        }

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"http={host}:{port};https={host}:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>;localhost;127.*", RegistryValueKind.String);
        key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        Refresh();
    }

    public static void Restore()
    {
        if (!File.Exists(BackupPath)) return;
        try
        {
            var encrypted = File.ReadAllBytes(BackupPath);
            var clear = ProtectedData.Unprotect(encrypted, ProfileStore.Entropy(), DataProtectionScope.CurrentUser);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(clear);
            if (snapshot is null) return;
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) return;
            RestoreValue(key, "ProxyEnable", snapshot.ProxyEnable, RegistryValueKind.DWord);
            RestoreValue(key, "ProxyServer", snapshot.ProxyServer, RegistryValueKind.String);
            RestoreValue(key, "ProxyOverride", snapshot.ProxyOverride, RegistryValueKind.String);
            RestoreValue(key, "AutoConfigURL", snapshot.AutoConfigURL, RegistryValueKind.String);
            File.Delete(BackupPath);
            Refresh();
        }
        catch { /* Keep the backup so a later app launch can retry restoration. */ }
    }

    public static void RecoverStaleProxy() => Restore();

    private static void RestoreValue(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, kind);
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
    }
}
