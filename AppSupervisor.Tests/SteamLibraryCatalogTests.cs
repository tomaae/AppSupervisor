using AppSupervisor.Steam;

namespace AppSupervisor.Tests;

/// <summary>Verifies Steam library parsing and main-executable candidate ranking.</summary>
public sealed class SteamLibraryCatalogTests
{
    /// <summary>Confirms escaped paths are decoded from a libraryfolders.vdf document.</summary>
    [Fact]
    public void ExtractLibraryPaths_MultipleLibraries_DecodesDistinctPaths()
    {
        const string text = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" }
                "1" { "path" "D:\\SteamLibrary" }
            }
            """;

        IReadOnlyList<string> paths = SteamLibraryCatalog.ExtractLibraryPaths(text);

        Assert.Contains(@"C:\Program Files (x86)\Steam", paths);
        Assert.Contains(@"D:\SteamLibrary", paths);
    }

    /// <summary>Confirms the item-name-matching root executable outranks crash handlers and nested tools.</summary>
    [Fact]
    public void FindExecutableCandidates_XsOverlayLayout_SelectsMainExecutableFirst()
    {
        string root = CreateTemporaryDirectory();

        try
        {
            string installDirectory = Path.Combine(root, "steamapps", "common", "XSOverlay_Beta");
            Directory.CreateDirectory(Path.Combine(installDirectory, "Launcher"));
            File.WriteAllText(Path.Combine(installDirectory, "XSOverlay.exe"), "");
            File.WriteAllText(Path.Combine(installDirectory, "UnityCrashHandler64.exe"), "");
            File.WriteAllText(
                Path.Combine(installDirectory, "Launcher", "XSOverlay Process Manager.exe"),
                ""
            );
            var item = new InstalledSteamItem(1173510, "XSOverlay", installDirectory, root);

            IReadOnlyList<string> candidates = SteamLibraryCatalog.FindExecutableCandidates(
                item,
                CancellationToken.None
            );

            Assert.Equal(Path.Combine(installDirectory, "XSOverlay.exe"), candidates[0]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>Creates an isolated directory owned by the current test.</summary>
    /// <returns>The new temporary directory path.</returns>
    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AppSupervisor.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes only the isolated directory created by this test.</summary>
    /// <param name="path">The validated temporary directory path.</param>
    private static void DeleteTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string expectedPrefix = Path.GetFullPath(Path.GetTempPath());

        if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("AppSupervisor.Tests.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Temporary test path validation failed.");
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
