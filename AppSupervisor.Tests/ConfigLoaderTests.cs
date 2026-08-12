using System.Text.Json;
using AppSupervisor.Configuration;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies strict JSON loading and per-helper semantic validation.
/// </summary>
public sealed class ConfigLoaderTests
{
    /// <summary>
    /// Confirms that a fresh installation receives a valid empty configuration automatically.
    /// </summary>
    [Fact]
    public void Load_MissingFile_CreatesEmptyConfiguration()
    {
        using var file = TemporaryConfigFile.CreateMissing();

        List<SupervisorProfileConfig> config = ConfigLoader.Load(file.Path).Profiles;

        Assert.Empty(config);
        Assert.True(File.Exists(file.Path));
        Assert.Contains("profiles", File.ReadAllText(file.Path));
    }

    /// <summary>
    /// Confirms that applications and services independently retain their configured notification targets.
    /// </summary>
    [Fact]
    public void Load_ValidConfiguration_LoadsPerHelperNotificationTargets()
    {
        using var file = TemporaryConfigFile.Create(new[]
        {
            new
            {
                name = "Valid",
                monitorProcess = "notepad.exe",
                applications = new[]
                {
                    new
                    {
                        path = Environment.ProcessPath!,
                        ensureClosedUntilNeeded = true,
                        notifications = new { target = new[] { "popup", "windows" } }
                    }
                },
                services = new[]
                {
                    new
                    {
                        enabled = false,
                        serviceName = "Example Service",
                        notifications = new { target = new[] { "xsoverlay" } }
                    }
                }
            }
        });

        List<SupervisorProfileConfig> config = ConfigLoader.Load(file.Path).Profiles;

        Assert.Equal(
            [NotificationTarget.Popup, NotificationTarget.Windows],
            config[0].Applications[0].Notifications.Target
        );
        Assert.True(config[0].Applications[0].EnsureClosedUntilNeeded);
        Assert.Equal(
            [NotificationTarget.XsOverlay],
            config[0].Services[0].Notifications.Target
        );
    }

    /// <summary>
    /// Confirms that duplicate destinations are rejected on the individual helper that declares them.
    /// </summary>
    [Fact]
    public void Load_DuplicateHelperTargets_ThrowsValidationError()
    {
        using var file = TemporaryConfigFile.Create(new[]
        {
            new
            {
                name = "Duplicate",
                monitorProcess = "notepad.exe",
                applications = new[]
                {
                    new
                    {
                        enabled = false,
                        notifications = new { target = new[] { "popup", "popup" } }
                    }
                },
                services = Array.Empty<object>()
            }
        });

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigLoader.Load(file.Path)
        );

        Assert.Contains("duplicate notification target", exception.Message);
    }

    /// <summary>
    /// Confirms that obsolete profile-level notification configuration cannot be silently ignored.
    /// </summary>
    [Fact]
    public void Load_ProfileLevelNotifications_ThrowsJsonError()
    {
        using var file = TemporaryConfigFile.Create(new[]
        {
            new
            {
                name = "Legacy",
                monitorProcess = "notepad.exe",
                notifications = new { target = new[] { "popup" } },
                applications = Array.Empty<object>(),
                services = Array.Empty<object>()
            }
        });

        Assert.Throws<JsonException>(() => ConfigLoader.Load(file.Path));
    }

    /// <summary>
    /// Confirms that the obsolete launchTarget property is rejected instead of silently disabling an App URI.
    /// </summary>
    [Fact]
    public void Load_LegacyLaunchTarget_ThrowsJsonError()
    {
        using var file = TemporaryConfigFile.Create(new[]
        {
            new
            {
                name = "Legacy launch property",
                monitorProcess = "notepad.exe",
                applications = new[]
                {
                    new
                    {
                        enabled = false,
                        launchTarget = "steam://rungameid/1173510"
                    }
                },
                services = Array.Empty<object>()
            }
        });

        Assert.Throws<JsonException>(() => ConfigLoader.Load(file.Path));
    }

    /// <summary>
    /// Confirms that an enabled helper cannot reference an executable that is absent from disk.
    /// </summary>
    [Fact]
    public void Load_MissingExecutable_ThrowsValidationError()
    {
        string missingPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"AppSupervisor-Missing-{Guid.NewGuid():N}.exe"
        );

        using var file = TemporaryConfigFile.Create(new[]
        {
            new
            {
                name = "MissingExecutable",
                monitorProcess = "notepad.exe",
                applications = new[]
                {
                    new
                    {
                        path = missingPath,
                        notifications = new { target = Array.Empty<string>() }
                    }
                },
                services = Array.Empty<object>()
            }
        });

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigLoader.Load(file.Path)
        );

        Assert.Contains("executable does not exist", exception.Message);
    }

    /// <summary>
    /// <summary>Confirms disabled profiles still require structurally valid application and service arrays.</summary>
    [Fact]
    public void Load_DisabledProfileWithNullApplications_ThrowsValidationError()
    {
        using var file = TemporaryConfigFile.Create(new object[]
        {
            new
            {
                enabled = false,
                name = "Disabled",
                monitorProcess = "notepad.exe",
                applications = (object?)null,
                services = Array.Empty<object>()
            }
        });

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigLoader.Load(file.Path)
        );

        Assert.Contains("must contain an applications array", exception.Message);
    }

    /// <summary>Confirms surrounding whitespace is normalized before matching and validation.</summary>
    [Fact]
    public void Load_WhitespaceAroundIdentifiers_NormalizesValues()
    {
        using var file = TemporaryConfigFile.Create(new[]
        {
            new
            {
                name = "  Normalized  ",
                monitorProcess = "  notepad.exe  ",
                applications = new[]
                {
                    new
                    {
                        path = $"  {Environment.ProcessPath!}  ",
                        notifications = new { target = Array.Empty<string>() }
                    }
                },
                services = Array.Empty<object>()
            }
        });

        List<SupervisorProfileConfig> config = ConfigLoader.Load(file.Path).Profiles;

        Assert.Equal("Normalized", config[0].Name);
        Assert.Equal("notepad.exe", config[0].MonitorProcess);
        Assert.Equal(Environment.ProcessPath, config[0].Applications[0].Path);
    }

    /// <summary>Confirms timeout values beyond the editor's supported range are rejected.</summary>
    [Fact]
    public void Load_ExcessiveTimeout_ThrowsValidationError()
    {
        using var file = TemporaryConfigFile.Create(new[]
        {
            new
            {
                enabled = false,
                name = "Excessive timeout",
                monitorProcess = "notepad.exe",
                closeTimeoutSeconds = 86_401,
                applications = Array.Empty<object>(),
                services = Array.Empty<object>()
            }
        });

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigLoader.Load(file.Path)
        );

        Assert.Contains("must be between 0 and 86400", exception.Message);
    }

    /// <summary>Confirms profile startup delays beyond the editor's supported range are rejected.</summary>
    [Fact]
    public void Load_ExcessiveProfileStartupDelay_ThrowsValidationError()
    {
        using var file = TemporaryConfigFile.Create(new[]
        {
            new
            {
                enabled = false,
                name = "Excessive startup delay",
                monitorProcess = "notepad.exe",
                waitBeforeStartingResourcesMilliseconds = 3_600_001,
                applications = Array.Empty<object>(),
                services = Array.Empty<object>()
            }
        });

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigLoader.Load(file.Path)
        );

        Assert.Contains(
            "waitBeforeStartingResourcesMilliseconds",
            exception.Message
        );
        Assert.Contains("must be between 0 and 3600000", exception.Message);
    }

    /// Owns one temporary JSON file and removes its isolated directory after a test.
    /// </summary>
    private sealed class TemporaryConfigFile : IDisposable
    {
        private readonly string _directoryPath;

        /// <summary>
        /// Creates a wrapper for an already written temporary configuration file.
        /// </summary>
        /// <param name="directoryPath">The isolated temporary directory.</param>
        /// <param name="path">The JSON file inside the directory.</param>
        private TemporaryConfigFile(string directoryPath, string path)
        {
            _directoryPath = directoryPath;
            Path = path;
        }

        /// <summary>
        /// Gets the temporary JSON file path.
        /// </summary>
        public string Path { get; }

        /// <summary>
        /// Serializes one configuration value into a fresh isolated temporary file.
        /// </summary>
        /// <param name="config">The anonymous configuration value to serialize.</param>
        /// <returns>An owner that removes the temporary directory when disposed.</returns>
        public static TemporaryConfigFile Create(object config)
        {
            string directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AppSupervisor.Tests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(directoryPath);

            string path = System.IO.Path.Combine(directoryPath, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                profiles = config,
                integrations = new { steamVr = new { enabled = false } }
            }));
            return new TemporaryConfigFile(directoryPath, path);
        }

        /// <summary>
        /// Creates an isolated directory whose configuration path does not exist yet.
        /// </summary>
        /// <returns>An owner that removes the generated configuration and directory when disposed.</returns>
        public static TemporaryConfigFile CreateMissing()
        {
            string directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AppSupervisor.Tests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(directoryPath);
            return new TemporaryConfigFile(
                directoryPath,
                System.IO.Path.Combine(directoryPath, "config.json")
            );
        }

        /// <summary>
        /// Removes the isolated temporary directory and its configuration file.
        /// </summary>
        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
