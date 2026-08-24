using System.Text.Json;

namespace AppSupervisor.StreamDeck;

internal sealed record StreamDeckPluginMetadata(string Identifier, string DisplayName);

/// <summary>Maps Stream Deck action UUIDs to the category and author labels shown by Stream Deck.</summary>
internal sealed class StreamDeckPluginCatalog(IReadOnlyList<StreamDeckPluginMetadata> plugins)
{
    private readonly IReadOnlyList<StreamDeckPluginMetadata> _plugins = plugins;

    public static StreamDeckPluginCatalog Load(string? cacheDirectory = null)
    {
        cacheDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Elgato",
            "StreamDeck",
            "PluginStoreCache"
        );
        var plugins = new List<StreamDeckPluginMetadata>();

        try
        {
            foreach (string path in Directory.EnumerateFiles(cacheDirectory, "*.json"))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                    JsonElement root = document.RootElement;
                    string identifier = ReadString(root, "identifier");
                    string name = ReadString(root, "name");

                    if (identifier.Length == 0 || name.Length == 0)
                        continue;

                    string developer = root.TryGetProperty("developer", out JsonElement value) &&
                        value.ValueKind == JsonValueKind.Object
                            ? ReadString(value, "name")
                            : "";
                    string displayName = developer.Length > 0 &&
                        !string.Equals(developer, "Elgato", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains($"[{developer}]", StringComparison.OrdinalIgnoreCase)
                            ? $"{name} [{developer}]"
                            : name;
                    plugins.Add(new StreamDeckPluginMetadata(identifier, displayName));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }

        return new StreamDeckPluginCatalog(plugins);
    }

    public string GetFullActionName(string actionUuid, string actionName)
    {
        string category = actionUuid.StartsWith(
            "com.elgato.streamdeck.",
            StringComparison.OrdinalIgnoreCase)
                ? "Stream Deck"
                : _plugins
                    .Where(plugin =>
                        string.Equals(actionUuid, plugin.Identifier, StringComparison.OrdinalIgnoreCase) ||
                        actionUuid.StartsWith(
                            plugin.Identifier + ".",
                            StringComparison.OrdinalIgnoreCase
                        ))
                    .OrderByDescending(plugin => plugin.Identifier.Length)
                    .Select(plugin => plugin.DisplayName)
                    .FirstOrDefault() ?? "";

        return category.Length == 0 ? actionName : $"{category}: {actionName}";
    }

    private static string ReadString(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
}
