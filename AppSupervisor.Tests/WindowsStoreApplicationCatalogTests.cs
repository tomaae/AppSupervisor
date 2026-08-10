using AppSupervisor.Store;

namespace AppSupervisor.Tests;

/// <summary>Verifies Windows package manifest application discovery and launch identity construction.</summary>
public sealed class WindowsStoreApplicationCatalogTests
{
    /// <summary>Confirms a full-trust package application yields its executable and AppsFolder identity.</summary>
    [Fact]
    public void ParseManifest_VrcFaceTrackingStylePackage_ReturnsLaunchableApplication()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.Tests.{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(Path.Combine(root, "VRCFaceTracking.exe"), "");
            File.WriteAllText(Path.Combine(root, "AppxManifest.xml"), """
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
                  <Properties>
                    <DisplayName>VRCFaceTracking</DisplayName>
                    <PublisherDisplayName>benaclejames</PublisherDisplayName>
                  </Properties>
                  <Applications>
                    <Application Id="App" Executable="VRCFaceTracking.exe">
                      <uap:VisualElements DisplayName="VRCFaceTracking" />
                    </Application>
                  </Applications>
                </Package>
                """);

            InstalledStoreApplication application = Assert.Single(
                WindowsStoreApplicationCatalog.ParseManifest(
                    Path.Combine(root, "AppxManifest.xml"),
                    "96ba052f",
                    "96ba052f_4s4k90pjvq32p",
                    root,
                    nonRemovable: false
                )
            );

            Assert.Equal("VRCFaceTracking", application.DisplayName);
            Assert.Equal(
                "shell:AppsFolder\\96ba052f_4s4k90pjvq32p!App",
                application.AppUri
            );
            Assert.Equal(Path.Combine(root, "VRCFaceTracking.exe"), application.ExecutablePath);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
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
