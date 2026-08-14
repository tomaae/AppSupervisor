using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppSupervisor.Steam;

/// <summary>Discovers installed Steam items and ranks executable candidates from their content folders.</summary>
internal static partial class SteamLibraryCatalog
{
    private static readonly string[] UnlikelyExecutableNames =
    [
        "crashhandler",
        "crashreport",
        "crashpad_handler",
        "unitycrashhandler",
        "uninstall",
        "unins",
        "setup",
        "vc_redist"
    ];

    /// <summary>Loads installed items from every library registered with the current Steam installation.</summary>
    /// <returns>Installed items ordered by display name and App ID.</returns>
    public static IReadOnlyList<InstalledSteamItem> LoadInstalledItems()
    {
        return DiscoverLibraryDirectories()
            .SelectMany(LoadLibraryItems)
            .GroupBy(item => item.AppId)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AppId)
            .ToArray();
    }

    /// <summary>Finds and ranks executable candidates beneath one installed item's content folder.</summary>
    /// <param name="item">The installed item whose executable should be identified.</param>
    /// <param name="cancellationToken">Cancels a potentially large directory traversal.</param>
    /// <returns>Candidate executable paths with the most likely main executable first.</returns>
    public static IReadOnlyList<string> FindExecutableCandidates(
        InstalledSteamItem item,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(item.InstallDirectory))
            return [];

        var candidates = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(item.InstallDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pendingDirectories.Pop();

            try
            {
                candidates.AddRange(Directory.EnumerateFiles(
                    directory,
                    "*.exe",
                    SearchOption.TopDirectoryOnly
                ));
            }
            catch
            {
                // An inaccessible subdirectory does not invalidate the rest of the installed item.
            }

            try
            {
                foreach (string childDirectory in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var information = new DirectoryInfo(childDirectory);

                    if ((information.Attributes & FileAttributes.ReparsePoint) == 0 &&
                        !string.Equals(
                            information.Name,
                            "_CommonRedist",
                            StringComparison.OrdinalIgnoreCase
                        ))
                    {
                        pendingDirectories.Push(childDirectory);
                    }
                }
            }
            catch
            {
                // Continue with directories that were already discovered.
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => ScoreExecutable(item, path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Finds a likely icon source using only the installation root and two child levels.</summary>
    /// <param name="item">The installed item whose presentation icon is requested.</param>
    /// <returns>The best shallow executable candidate, or null when none is available.</returns>
    internal static string? FindIconExecutableCandidate(InstalledSteamItem item)
    {
        if (!Directory.Exists(item.InstallDirectory))
            return null;

        IReadOnlyList<string> directories = [item.InstallDirectory];

        for (int depth = 0; depth <= 2 && directories.Count > 0; depth++)
        {
            var candidates = new List<string>();
            var nextDirectories = new List<string>();

            foreach (string directory in directories)
            {
                try
                {
                    candidates.AddRange(Directory.EnumerateFiles(
                        directory,
                        "*.exe",
                        SearchOption.TopDirectoryOnly
                    ));
                }
                catch
                {
                    // Other readable directories at the same depth can still provide an icon.
                }

                if (depth == 2)
                    continue;

                try
                {
                    foreach (string childDirectory in Directory.EnumerateDirectories(directory))
                    {
                        var information = new DirectoryInfo(childDirectory);

                        if ((information.Attributes & FileAttributes.ReparsePoint) == 0 &&
                            !string.Equals(
                                information.Name,
                                "_CommonRedist",
                                StringComparison.OrdinalIgnoreCase
                            ))
                        {
                            nextDirectories.Add(childDirectory);
                        }
                    }
                }
                catch
                {
                    // Continue with child directories found elsewhere at this depth.
                }
            }

            if (candidates.Count > 0)
            {
                return candidates
                    .OrderByDescending(path => ScoreExecutable(item, path))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .First();
            }

            directories = nextDirectories;
        }

        return null;
    }

    /// <summary>Extracts registered Steam library paths from a libraryfolders.vdf document.</summary>
    /// <param name="text">The complete VDF document.</param>
    /// <returns>Distinct decoded library directory paths.</returns>
    internal static IReadOnlyList<string> ExtractLibraryPaths(string text)
    {
        return LibraryPathRegex().Matches(text)
            .Select(match => DecodeVdfValue(match.Groups["value"].Value))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Parses one installed-item manifest using its owning Steam library.</summary>
    /// <param name="manifestPath">The appmanifest_*.acf path.</param>
    /// <param name="libraryDirectory">The Steam library containing the manifest.</param>
    /// <returns>The installed item, or <see langword="null"/> when required values are invalid.</returns>
    internal static InstalledSteamItem? ParseManifest(
        string manifestPath,
        string libraryDirectory)
    {
        try
        {
            string text = File.ReadAllText(manifestPath);
            string? appIdText = ReadVdfValue(text, "appid");
            string? name = ReadVdfValue(text, "name");
            string? installDirectoryName = ReadVdfValue(text, "installdir");

            if (!ulong.TryParse(appIdText, out ulong appId) ||
                appId == 0 ||
                string.IsNullOrWhiteSpace(name) ||
                string.IsNullOrWhiteSpace(installDirectoryName))
            {
                return null;
            }

            string installDirectory = Path.GetFullPath(Path.Combine(
                libraryDirectory,
                "steamapps",
                "common",
                installDirectoryName
            ));

            return Directory.Exists(installDirectory)
                ? new InstalledSteamItem(appId, name, installDirectory, libraryDirectory)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds the current Steam root and every library registered by it.</summary>
    /// <returns>Existing, distinct Steam library directories.</returns>
    private static IReadOnlyList<string> DiscoverLibraryDirectories()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRegistrySteamPath(roots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");

        string programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86
        );
        roots.Add(Path.Combine(programFilesX86, "Steam"));

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots.Where(Directory.Exists))
        {
            libraries.Add(Path.GetFullPath(root));
            string libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");

            if (!File.Exists(libraryFile))
                continue;

            try
            {
                foreach (string library in ExtractLibraryPaths(File.ReadAllText(libraryFile)))
                {
                    if (Directory.Exists(library))
                        libraries.Add(Path.GetFullPath(library));
                }
            }
            catch
            {
                // The root library remains usable when the registry file cannot be read.
            }
        }

        return libraries.ToArray();
    }

    /// <summary>Adds one Steam path read from a registry value when present.</summary>
    /// <param name="paths">The destination path set.</param>
    /// <param name="registryRoot">The registry hive containing the Steam key.</param>
    /// <param name="subKeyName">The Steam subkey name.</param>
    /// <param name="valueName">The path value name.</param>
    private static void AddRegistrySteamPath(
        ISet<string> paths,
        RegistryKey registryRoot,
        string subKeyName,
        string valueName)
    {
        try
        {
            using RegistryKey? key = registryRoot.OpenSubKey(subKeyName);

            if (key?.GetValue(valueName) is string path && !string.IsNullOrWhiteSpace(path))
                paths.Add(path);
        }
        catch
        {
            // Default installation discovery remains available when registry access fails.
        }
    }

    /// <summary>Loads every valid installed-item manifest from one Steam library.</summary>
    /// <param name="libraryDirectory">The Steam library directory.</param>
    /// <returns>Valid installed items found in the library.</returns>
    private static IEnumerable<InstalledSteamItem> LoadLibraryItems(string libraryDirectory)
    {
        string steamAppsDirectory = Path.Combine(libraryDirectory, "steamapps");
        IEnumerable<string> manifests;

        try
        {
            manifests = Directory.EnumerateFiles(
                steamAppsDirectory,
                "appmanifest_*.acf",
                SearchOption.TopDirectoryOnly
            ).ToArray();
        }
        catch
        {
            return [];
        }

        return manifests
            .Select(path => ParseManifest(path, libraryDirectory))
            .OfType<InstalledSteamItem>()
            .Select(item => item with
            {
                IconExecutablePath = FindIconExecutableCandidate(item)
            })
            .ToArray();
    }

    /// <summary>Reads one quoted scalar from a Valve KeyValues text document.</summary>
    /// <param name="text">The VDF or ACF document.</param>
    /// <param name="key">The scalar key to find.</param>
    /// <returns>The decoded value, or <see langword="null"/> when absent.</returns>
    private static string? ReadVdfValue(string text, string key)
    {
        Match match = Regex.Match(
            text,
            "\"" + Regex.Escape(key) + "\"\\s+\"(?<value>(?:\\\\.|[^\"])*)\"",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
        );
        return match.Success ? DecodeVdfValue(match.Groups["value"].Value) : null;
    }

    /// <summary>Decodes the backslash escapes used by Steam KeyValues path and text fields.</summary>
    /// <param name="value">The raw quoted-field contents.</param>
    /// <returns>The decoded scalar value.</returns>
    private static string DecodeVdfValue(string value)
    {
        return value
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);
    }

    /// <summary>Scores how likely an executable is to be the selected Steam item's main process.</summary>
    /// <param name="item">The installed Steam item.</param>
    /// <param name="executablePath">The executable candidate.</param>
    /// <returns>A relative score used only for ordering candidates.</returns>
    private static int ScoreExecutable(InstalledSteamItem item, string executablePath)
    {
        string executableName = Path.GetFileNameWithoutExtension(executablePath);
        string normalizedItemName = NormalizeName(item.Name);
        string normalizedExecutableName = NormalizeName(executableName);
        int score = 0;

        if (normalizedExecutableName == normalizedItemName)
            score += 1000;
        else if (normalizedExecutableName.Contains(normalizedItemName, StringComparison.Ordinal) ||
                 normalizedItemName.Contains(normalizedExecutableName, StringComparison.Ordinal))
            score += 500;

        if (string.Equals(
            Path.GetDirectoryName(executablePath),
            item.InstallDirectory,
            StringComparison.OrdinalIgnoreCase
        ))
        {
            score += 300;
        }

        if (UnlikelyExecutableNames.Any(name =>
            executableName.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            score -= 1000;
        }

        string relativePath = Path.GetRelativePath(item.InstallDirectory, executablePath);
        score -= relativePath.Count(character =>
            character == Path.DirectorySeparatorChar ||
            character == Path.AltDirectorySeparatorChar) * 10;
        return score;
    }

    /// <summary>Removes punctuation and casing differences for executable-to-item name comparison.</summary>
    /// <param name="value">The display or executable name.</param>
    /// <returns>A lowercase alphanumeric comparison key.</returns>
    private static string NormalizeName(string value)
    {
        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    /// <summary>Matches path values in Steam's libraryfolders.vdf document.</summary>
    [GeneratedRegex("\\\"path\\\"\\s+\\\"(?<value>(?:\\\\\\\\.|[^\\\"])*)\\\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();
}
