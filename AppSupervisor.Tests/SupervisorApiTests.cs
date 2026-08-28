using System.Text.Json;
using AppSupervisor.SupervisorApi;

namespace AppSupervisor.Tests;

/// <summary>Verifies the read-only cached Supervisor API route contract.</summary>
public sealed class SupervisorApiTests
{
    [Fact]
    public void Route_RequestedHierarchy_ReturnsCachedJsonDocuments()
    {
        using var server = new SupervisorApiServer();
        server.Publish(CreateSnapshot());

        using JsonDocument root = ParseOk(server.Route("GET", "/"));
        JsonElement profileLink = Assert.Single(root.RootElement.GetProperty("profiles").EnumerateArray());
        Assert.Equal("profile-id", profileLink.GetProperty("internalId").GetString());
        Assert.Equal("/profile-id", profileLink.GetProperty("endpoint").GetString());

        using JsonDocument profile = ParseOk(server.Route("GET", "/profile-id"));
        Assert.Equal("process", profile.RootElement.GetProperty("triggerType").GetString());
        Assert.Equal("VRChat.exe", profile.RootElement.GetProperty("monitorProcess").GetString());
        Assert.Equal(
            "",
            profile.RootElement.GetProperty("monitorBluetoothDeviceId").GetString()
        );
        JsonElement helperLink = Assert.Single(profile.RootElement.GetProperty("helpers").EnumerateArray());
        Assert.Equal("helper-id", helperLink.GetProperty("internalId").GetString());
        Assert.True(helperLink.GetProperty("active").GetBoolean());
        Assert.Equal(1, helperLink.GetProperty("healthChecksConfigured").GetInt32());

        using JsonDocument helper = ParseOk(server.Route("GET", "/profile-id/helper-id"));
        Assert.Equal("helper.exe", helper.RootElement.GetProperty("name").GetString());
        Assert.Equal("/profile-id/helper-id/healthcheck",
            helper.RootElement.GetProperty("healthCheckEndpoint").GetString());

        using JsonDocument health = ParseOk(server.Route(
            "GET",
            "/profile-id/helper-id/healthcheck"
        ));
        JsonElement check = Assert.Single(
            health.RootElement.GetProperty("healthChecks").EnumerateArray()
        );
        Assert.Equal("healthy", check.GetProperty("status").GetString());
        Assert.Equal("Listener is available.", check.GetProperty("detail").GetString());

        using JsonDocument macro = ParseOk(server.Route(
            "GET",
            "/profile-id/helper-id/macro"
        ));
        Assert.True(macro.RootElement.GetProperty("configured").GetBoolean());
        Assert.Equal("idle", macro.RootElement.GetProperty("status").GetString());
        Assert.Single(macro.RootElement.GetProperty("actions").EnumerateArray());
    }

    [Fact]
    public void Route_WriteAndUnknownRoutes_AreRejected()
    {
        using var server = new SupervisorApiServer();
        server.Publish(CreateSnapshot());

        Assert.Equal(405, server.Route("POST", "/").StatusCode);
        Assert.Equal(404, server.Route("GET", "/missing").StatusCode);
        Assert.Equal(404, server.Route("GET", "/profile-id/helper-id/unknown").StatusCode);
    }

    [Fact]
    public void SnapshotFactory_DisabledConfiguration_UsesNoRuntimeQueries()
    {
        var configuration = new AppSupervisorConfig
        {
            Integrations = new IntegrationsConfig
            {
                SupervisorApi = new SupervisorApiConfig { Enabled = true }
            },
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    ProfileId = "disabled-profile",
                    Name = "Disabled",
                    Enabled = false,
                    MonitorProcess = "monitor.exe",
                    Applications =
                    [
                        new ManagedApplicationConfig
                        {
                            ResourceId = "stable-helper",
                            Path = @"C:\Tools\helper.exe",
                            StartupMacros =
                            [
                                new StartupMacroActionConfig
                                {
                                    Type = StartupMacroActionType.Delay,
                                    DelayMilliseconds = 50
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        SupervisorApiSnapshot snapshot = SupervisorApiSnapshotFactory.Create(
            configuration,
            [],
            paused: false
        );

        SupervisorApiProfileSnapshot profile = Assert.Single(snapshot.Profiles);
        Assert.Equal("disabled", profile.Status);
        Assert.Equal("process", profile.TriggerType);
        Assert.Equal("", profile.MonitorBluetoothDeviceId);
        SupervisorApiHelperSnapshot helper = Assert.Single(profile.Helpers);
        Assert.Equal("stable-helper", helper.InternalId);
        Assert.False(helper.Active);
        Assert.True(helper.Macro.Configured);
    }

    [Fact]
    public void ConfigLoader_LegacyProfileWithoutId_GeneratesInternalId()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.SupervisorApiTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");

        try
        {
            File.WriteAllText(path, """
                {
                  "profiles": [
                    {
                      "name": "Legacy profile",
                      "monitorProcess": "legacy.exe"
                    }
                  ],
                  "integrations": {}
                }
                """);

            SupervisorProfileConfig profile = Assert.Single(ConfigLoader.Load(path).Profiles);
            Assert.Matches("^[a-f0-9]{32}$", profile.ProfileId);
            Assert.DoesNotContain(' ', profile.ProfileId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonDocument ParseOk(SupervisorApiResponse response)
    {
        Assert.Equal(200, response.StatusCode);
        return JsonDocument.Parse(response.Body);
    }

    private static SupervisorApiSnapshot CreateSnapshot() => new(
        new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc),
        Paused: false,
        Profiles:
        [
            new SupervisorApiProfileSnapshot(
                "VR profile",
                "profile-id",
                Enabled: true,
                Status: "active",
                TriggerType: "process",
                MonitorProcess: "VRChat.exe",
                MonitorBluetoothDeviceId: "",
                Helpers:
                [
                    new SupervisorApiHelperSnapshot(
                        "helper.exe",
                        "helper-id",
                        Enabled: true,
                        Active: true,
                        Status: "active",
                        Path: @"C:\Tools\helper.exe",
                        AppUri: "",
                        Arguments: "--quiet",
                        Restart: true,
                        EnsureClosedUntilNeeded: false,
                        LeaveRunningAfterProfileStops: false,
                        MinimizeAfterStart: false,
                        MonitorResponsiveness: false,
                        HealthChecks:
                        [
                            new SupervisorApiHealthCheckSnapshot(
                                "Listener",
                                Enabled: true,
                                Active: true,
                                Status: "healthy",
                                Detail: "Listener is available.",
                                Type: HealthCheckType.Listener,
                                Protocol: ListenerProtocol.Tcp,
                                Port: 9000,
                                ActiveWhenProcess: "",
                                IntervalSeconds: 10,
                                TimeoutSeconds: 3,
                                FailureThreshold: 3,
                                StartupDelaySeconds: 10,
                                RestartOnFailure: true,
                                Parameters: [],
                                StaleSeconds: null
                            )
                        ],
                        Macro: new SupervisorApiMacroSnapshot(
                            Configured: true,
                            Status: "idle",
                            Actions:
                            [
                                new StartupMacroActionConfig
                                {
                                    Type = StartupMacroActionType.Delay,
                                    DelayMilliseconds = 100
                                }
                            ]
                        )
                    )
                ]
            )
        ]
    );
}
