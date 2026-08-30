using System.Reflection;
using System.Text.Json;

namespace AppSupervisor.Bluetooth;

/// <summary>Resolves Bluetooth SIG company identifiers from the bundled assigned-numbers snapshot.</summary>
internal static class BluetoothCompanyIdentifiers
{
    private const string ResourceName = "AppSupervisor.Bluetooth.Data.company_ids.json";
    private static readonly Lazy<IReadOnlyDictionary<ushort, string>> Names = new(LoadNames);

    /// <summary>Formats distinct company identifiers as human-readable advertising-data hints.</summary>
    internal static string Format(IEnumerable<ushort>? companyIds)
    {
        ushort[] ids = companyIds?
            .Distinct()
            .OrderBy(id => id)
            .ToArray() ?? [];
        if (ids.Length == 0)
            return "—";

        return string.Join(", ", ids.Select(id =>
            Names.Value.TryGetValue(id, out string? name)
                ? name
                : $"Company ID 0x{id:X4}"
        ));
    }

    private static IReadOnlyDictionary<ushort, string> LoadNames()
    {
        Assembly assembly = typeof(BluetoothCompanyIdentifiers).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName) ??
            throw new InvalidOperationException(
                $"Embedded Bluetooth company lookup '{ResourceName}' is missing."
            );
        CompanyIdentifier[] entries = JsonSerializer.Deserialize<CompanyIdentifier[]>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        ) ?? [];

        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(entry => entry.Code)
            .ToDictionary(group => group.Key, group => group.First().Name.Trim());
    }

    private sealed record CompanyIdentifier(ushort Code, string Name);
}
