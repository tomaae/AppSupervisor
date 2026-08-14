using AppSupervisor.Configuration;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies shared JSON serialization, atomic replacement, and verified shutdown backup behavior.
/// </summary>
public sealed class ConfigFileWriterTests
{
    /// <summary>Confirms saved property and enum names match the strict camel-case loader contract.</summary>
    [Fact]
    public void Serialize_VrcOscConfiguration_UsesLoaderCompatibleNames()
    {
        AppSupervisorConfig configuration = CreateValidConfiguration();

        string json = ConfigFileWriter.Serialize(configuration);

        Assert.Contains("\"monitorProcess\"", json);
        Assert.Contains("\"healthChecks\"", json);
        Assert.Contains("\"type\": \"vrcosc\"", json);
        Assert.DoesNotContain("Vrcosc", json);
    }

    /// <summary>Confirms atomic save produces a loadable document and leaves no same-directory temporary file.</summary>
    [Fact]
    public void SaveAtomic_ValidConfiguration_RoundTripsWithoutTemporaryFile()
    {
        using var directory = TemporaryDirectory.Create();
        string path = Path.Combine(directory.Path, "config.json");

        ConfigFileWriter.SaveAtomic(path, CreateValidConfiguration());
        List<SupervisorProfileConfig> loaded = ConfigLoader.Load(path).Profiles;

        Assert.Single(loaded);
        Assert.Equal("Writer test", loaded[0].Name);
        Assert.True(loaded[0].Applications[0].LeaveRunningAfterProfileStops);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    /// <summary>Confirms shutdown backup is generated from verified memory without copying corrupt live-file text.</summary>
    [Fact]
    public void SaveVerifiedBackup_CorruptLiveFile_WritesValidOldFile()
    {
        using var directory = TemporaryDirectory.Create();
        string configPath = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(configPath, "{ this is corrupt");

        string backupPath = VerifiedConfigBackup.Save(
            configPath,
            CreateValidConfiguration()
        );

        Assert.Equal(configPath + ".old", backupPath);
        Assert.Equal("{ this is corrupt", File.ReadAllText(configPath));
        Assert.Single(ConfigLoader.Load(backupPath).Profiles);
    }

    /// <summary>Creates one fully valid document including a structural vrcosc check.</summary>
    /// <returns>The configuration used by writer and backup tests.</returns>
    private static AppSupervisorConfig CreateValidConfiguration()
    {
        return new AppSupervisorConfig
        {
            Profiles =
            [
            new SupervisorProfileConfig
            {
                Name = "Writer test",
                MonitorProcess = "notepad.exe",
                Applications =
                [
                    new ManagedApplicationConfig
                    {
                        Path = Environment.ProcessPath!,
                        LeaveRunningAfterProfileStops = true,
                        Notifications = new NotificationConfig { Target = [] },
                        HealthChecks =
                        [
                            new HealthCheckConfig
                            {
                                Name = "VRChat OSCQuery",
                                Type = HealthCheckType.Vrcosc,
                                Notifications = new NotificationConfig
                                {
                                    Target = [NotificationTarget.Windows]
                                }
                            }
                        ]
                    }
                ],
                Services = []
            }
            ]
        };
    }

    /// <summary>Owns one isolated temporary directory for filesystem tests.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>Creates an owner for an existing temporary directory.</summary>
        /// <param name="path">The directory path.</param>
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        /// <summary>Gets the temporary directory path.</summary>
        public string Path { get; }

        /// <summary>Creates a fresh isolated temporary directory.</summary>
        /// <returns>The directory owner.</returns>
        public static TemporaryDirectory Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AppSupervisor.WriterTests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        /// <summary>Removes the temporary directory and all test-owned files.</summary>
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
