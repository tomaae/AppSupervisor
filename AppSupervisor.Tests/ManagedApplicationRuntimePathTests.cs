using AppSupervisor.Configuration;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies that launcher invocation and persistent process identity remain distinct.</summary>
public sealed class ManagedApplicationRuntimePathTests
{
    [Fact]
    public void Constructor_Launch4jWrapper_UsesBundledJavawForSupervision()
    {
        using var installation = new TestInstallation(
            "MZ\0Launch4j\0configuration\0bin\\javaw.exe\0"
        );
        var config = new ManagedApplicationConfig
        {
            Path = installation.LauncherPath,
            Arguments = "quiet"
        };

        using var application = new ManagedApplication(config, TimeSpan.Zero);

        Assert.Equal(installation.RuntimePath, application.RuntimePath);
        Assert.Equal(installation.LauncherPath, application.Config.Path);
        Assert.Equal("quiet", application.Config.Arguments);
    }

    [Fact]
    public void Constructor_OrdinaryExecutable_UsesConfiguredPathForSupervision()
    {
        using var installation = new TestInstallation("MZ\0ordinary application\0");
        var config = new ManagedApplicationConfig { Path = installation.LauncherPath };

        using var application = new ManagedApplication(config, TimeSpan.Zero);

        Assert.Equal(installation.LauncherPath, application.RuntimePath);
    }

    private sealed class TestInstallation : IDisposable
    {
        private readonly string _root;

        public TestInstallation(string launcherContent)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                $"AppSupervisor.ManagedApplicationRuntimePathTests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(_root);
            LauncherPath = Path.Combine(_root, "launcher.exe");
            RuntimePath = Path.Combine(_root, "jre", "bin", "javaw.exe");
            File.WriteAllText(LauncherPath, launcherContent);
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimePath)!);
            File.WriteAllText(RuntimePath, "runtime");
        }

        public string LauncherPath { get; }

        public string RuntimePath { get; }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
