using System.Text.Json;
using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies type-specific health-check configuration and exact JSON enum names.
/// </summary>
public sealed class HealthCheckConfigTests
{
    /// <summary>Confirms vrcosc needs no endpoint fields and retains configurable face parameter freshness.</summary>
    [Fact]
    public void Load_VrcOscWithoutPortOrProtocol_LoadsFaceParameters()
    {
        using var file = HealthConfigFile.Create(CreateProfile(new
        {
            name = "Face freshness",
            type = "vrcosc",
            parameters = new[] { "JawOpen", "LipPucker" },
            staleSeconds = 25,
            notifications = new { target = new[] { "xsoverlay" } }
        }));

        List<SupervisorProfileConfig> config = ConfigLoader.Load(file.Path);
        HealthCheckConfig check = config[0].Applications[0].HealthChecks[0];

        Assert.Equal(HealthCheckType.Vrcosc, check.Type);
        Assert.Null(check.Protocol);
        Assert.Null(check.Port);
        Assert.Equal(["JawOpen", "LipPucker"], check.Parameters);
        Assert.Equal(25, check.StaleSeconds);
    }

    /// <summary>Confirms vrcosc rejects endpoint and process-gating fields because OSCQuery and VRChat supply them.</summary>
    [Fact]
    public void Load_VrcOscWithEndpointFields_ThrowsValidationError()
    {
        using var file = HealthConfigFile.Create(CreateProfile(new
        {
            name = "Invalid vrcosc",
            type = "vrcosc",
            protocol = "udp",
            port = 9000,
            activeWhenProcess = "Something.exe",
            parameters = Array.Empty<string>(),
            notifications = new { target = Array.Empty<string>() }
        }));

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigLoader.Load(file.Path)
        );

        Assert.Contains("cannot configure protocol", exception.Message);
        Assert.Contains("cannot configure port", exception.Message);
        Assert.Contains("always bound to VRChat.exe", exception.Message);
    }

    /// <summary>Confirms listener checks require a valid port and protocol while accepting an optional process gate.</summary>
    [Fact]
    public void Load_InvalidListenerPort_ThrowsValidationError()
    {
        using var file = HealthConfigFile.Create(CreateProfile(new
        {
            name = "Invalid listener",
            type = "listener",
            protocol = "tcp",
            port = 70000,
            activeWhenProcess = "VRChat.exe",
            parameters = Array.Empty<string>(),
            notifications = new { target = Array.Empty<string>() }
        }));

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigLoader.Load(file.Path)
        );

        Assert.Contains("port must be between 1 and 65535", exception.Message);
    }

    /// <summary>Confirms duplicate check names are rejected within one helper regardless of casing.</summary>
    [Fact]
    public void Load_DuplicateHealthCheckNames_ThrowsValidationError()
    {
        object first = CreateListener("Listener");
        object second = CreateListener("listener");
        using var file = HealthConfigFile.Create(CreateProfile(first, second));

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigLoader.Load(file.Path)
        );

        Assert.Contains("duplicates health-check name", exception.Message);
    }

    /// <summary>Confirms removed recovery and startup-grace properties are rejected instead of silently ignored.</summary>
    /// <param name="propertyName">The obsolete JSON property to add to an otherwise valid listener.</param>
    [Theory]
    [InlineData("recoveryThreshold")]
    [InlineData("startupGraceSeconds")]
    public void Load_ObsoleteHealthProperty_ThrowsJsonError(string propertyName)
    {
        var listener = new Dictionary<string, object?>
        {
            ["name"] = "Legacy listener",
            ["type"] = "listener",
            ["protocol"] = "tcp",
            ["port"] = 12345,
            ["parameters"] = Array.Empty<string>(),
            ["notifications"] = new { target = Array.Empty<string>() },
            [propertyName] = 2
        };
        using var file = HealthConfigFile.Create(CreateProfile(listener));

        Assert.Throws<JsonException>(() => ConfigLoader.Load(file.Path));
    }

    /// <summary>Creates a valid listener-shaped anonymous configuration object.</summary>
    /// <param name="name">The check name.</param>
    /// <returns>A serializable listener check.</returns>
    private static object CreateListener(string name)
    {
        return new
        {
            name,
            type = "listener",
            protocol = "tcp",
            port = 12345,
            parameters = Array.Empty<string>(),
            notifications = new { target = Array.Empty<string>() }
        };
    }

    /// <summary>Creates one valid profile with the supplied health checks attached to an enabled helper.</summary>
    /// <param name="healthChecks">The anonymous health-check values.</param>
    /// <returns>A serializable top-level configuration array.</returns>
    private static object CreateProfile(params object[] healthChecks)
    {
        return new[]
        {
            new
            {
                name = "Health",
                monitorProcess = "notepad.exe",
                applications = new[]
                {
                    new
                    {
                        path = Environment.ProcessPath!,
                        notifications = new { target = Array.Empty<string>() },
                        healthChecks
                    }
                },
                services = Array.Empty<object>()
            }
        };
    }

    /// <summary>Owns one isolated temporary JSON file used by configuration-loading tests.</summary>
    private sealed class HealthConfigFile : IDisposable
    {
        private readonly string _directoryPath;

        /// <summary>Creates an owner for a previously written file.</summary>
        /// <param name="directoryPath">The temporary directory.</param>
        /// <param name="path">The JSON file path.</param>
        private HealthConfigFile(string directoryPath, string path)
        {
            _directoryPath = directoryPath;
            Path = path;
        }

        /// <summary>Gets the temporary configuration path.</summary>
        public string Path { get; }

        /// <summary>Serializes a configuration value into a fresh isolated directory.</summary>
        /// <param name="config">The configuration value to serialize.</param>
        /// <returns>An owner that cleans up the file.</returns>
        public static HealthConfigFile Create(object config)
        {
            string directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"AppSupervisor.HealthTests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(directoryPath);
            string path = System.IO.Path.Combine(directoryPath, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(config));
            return new HealthConfigFile(directoryPath, path);
        }

        /// <summary>Removes the temporary file and its isolated directory.</summary>
        public void Dispose()
        {
            if (Directory.Exists(_directoryPath))
                Directory.Delete(_directoryPath, recursive: true);
        }
    }
}
