using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>Verifies cheap, conservative Launch4j bundled-runtime detection.</summary>
public sealed class JavaLauncherDetectorTests
{
    [Fact]
    public void ResolveRuntimePath_Launch4jMarkersAndBundledJavaw_ReturnsJavaw()
    {
        using var installation = new TestInstallation(
            "MZ\0Launch4j\0configuration\0bin\\javaw.exe\0"
        );

        string result = JavaLauncherDetector.ResolveRuntimePath(installation.LauncherPath);

        Assert.Equal(installation.RuntimePath, result);
    }

    [Fact]
    public void ResolveRuntimePath_NonLaunch4jExecutable_ReturnsConfiguredPath()
    {
        using var installation = new TestInstallation("MZ\0ordinary application\0");

        string result = JavaLauncherDetector.ResolveRuntimePath(installation.LauncherPath);

        Assert.Equal(installation.LauncherPath, result);
    }

    [Fact]
    public void ResolveRuntimePath_MissingBundledJavaw_ReturnsConfiguredPathWithoutScanning()
    {
        using var installation = new TestInstallation(
            "MZ\0Launch4j\0bin\\javaw.exe\0",
            createRuntime: false
        );

        string result = JavaLauncherDetector.ResolveRuntimePath(installation.LauncherPath);

        Assert.Equal(installation.LauncherPath, result);
    }

    [Fact]
    public void ResolveRuntimePath_MarkersBeyondBoundedHeader_ReturnsConfiguredPath()
    {
        using var installation = new TestInstallation(
            new string('x', 256 * 1024) + "Launch4j\0bin\\javaw.exe"
        );

        string result = JavaLauncherDetector.ResolveRuntimePath(installation.LauncherPath);

        Assert.Equal(installation.LauncherPath, result);
    }

    private sealed class TestInstallation : IDisposable
    {
        private readonly string _root;

        public TestInstallation(string launcherContent, bool createRuntime = true)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                $"AppSupervisor.JavaLauncherDetectorTests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(_root);
            LauncherPath = Path.Combine(_root, "launcher.exe");
            RuntimePath = Path.Combine(_root, "jre", "bin", "javaw.exe");
            File.WriteAllText(LauncherPath, launcherContent);

            if (createRuntime)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RuntimePath)!);
                File.WriteAllText(RuntimePath, "runtime");
            }
        }

        public string LauncherPath { get; }

        public string RuntimePath { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
