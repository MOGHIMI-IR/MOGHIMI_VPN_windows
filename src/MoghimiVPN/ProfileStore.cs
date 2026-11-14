using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MoghimiVPN;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static ProfileStore Instance { get; } = new();
    public PersistedState State { get; private set; } = new();
    public string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoghimiVPN");
    public string ConfigDirectory => Path.Combine(DataDirectory, "configs");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
    public string StateFile => Path.Combine(DataDirectory, "profile-store.dpapi");

    private ProfileStore() { }

    public void Load()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        try
        {
            if (!File.Exists(StateFile)) { State = new(); return; }
            var encrypted = File.ReadAllBytes(StateFile);
            var clear = ProtectedData.Unprotect(encrypted, Entropy(), DataProtectionScope.CurrentUser);
            State = JsonSerializer.Deserialize<PersistedState>(clear, JsonOptions) ?? new();
        }
        catch
        {
            // Never delete an unreadable profile vault automatically. Preserve the file for recovery.
            State = new();
        }
    }

    public void ClearTransientConfigs()
    {
        try
        {
            if (Directory.Exists(ConfigDirectory))
                foreach (var file in Directory.EnumerateFiles(ConfigDirectory, "*.json"))
                    File.Delete(file);
        }
        catch { }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(State, JsonOptions);
        var encrypted = ProtectedData.Protect(clear, Entropy(), DataProtectionScope.CurrentUser);
        var temp = StateFile + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        if (File.Exists(StateFile)) File.Replace(temp, StateFile, null);
        else File.Move(temp, StateFile);
    }

    public static byte[] Entropy() => Encoding.UTF8.GetBytes("MoghimiVPN|DPAPI|v1|profile-vault");
}
