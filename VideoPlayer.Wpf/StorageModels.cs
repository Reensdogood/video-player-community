using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace VideoPlayer.Wpf;

public enum StorageKind
{
    Smb,
    WebDav,
    LocalFolder
}

public enum CloudProviderKind
{
    GoogleDrive,
    OneDrive,
    Dropbox,
    NaverMyBox
}

public sealed class StorageConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public StorageKind Kind { get; set; }
    public string Endpoint { get; set; } = "";
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public CloudProviderKind? CloudProvider { get; set; }
}

public sealed record StorageEntry(string Name, string Path, bool IsFolder, long? Size = null, string? PlaybackUri = null, CloudProviderKind? Provider = null)
{
    public string DisplaySize => !IsFolder && Size is long size ? (size > 0 && size < 10486 ? "< 0.01 MB" : $"{size / 1048576d:N2} MB") : "";
}

public sealed class CloudAccount
{
    public CloudProviderKind Provider { get; set; }
    public string ClientId { get; set; } = "";
    public string ProtectedRefreshToken { get; set; } = "";
}

public sealed class SavedSettings
{
    public string? SelectedLocalModel { get; set; }
    public string? CaptureFolder { get; set; }
    public string SubtitleFontFamily { get; set; } = "Segoe UI";
    public int SubtitleFontSize { get; set; } = 28;
    public bool SubtitleBold { get; set; }
    public string SubtitleOutline { get; set; } = "표준";
    public List<StorageConnection> StorageConnections { get; set; } = [];
    public List<CloudAccount> CloudAccounts { get; set; } = [];
}

public static class AppSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ohmyplayer", "settings.json");
    private static readonly string LegacySettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoraePlayer", "settings.json");

    public static SavedSettings Load()
    {
        try
        {
            var path = File.Exists(SettingsPath) ? SettingsPath : LegacySettingsPath;
            if (!File.Exists(path)) return new SavedSettings();
            var settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(path)) ?? new SavedSettings();
            if (path != SettingsPath) Save(settings);
            return settings;
        }
        catch
        {
            return new SavedSettings();
        }
    }

    public static void Save(SavedSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, true);
    }
}

public static class UserSecret
{
    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
