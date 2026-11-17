using System.IO;
using System.Text.Json;

namespace VideoPlayer.Wpf;

/// <summary>
/// OAuth client identifiers belong to the product integration, not to end users.
/// The product release may ship cloud-providers.json beside the executable with
/// GoogleDrive, OneDrive, and Dropbox client IDs after those apps are registered.
/// </summary>
public static class OAuthProviderConfig
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Clients = new(Load);

    public static string? GetClientId(CloudProviderKind provider)
    {
        return Clients.Value.TryGetValue(provider.ToString(), out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "cloud-providers.json");
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
