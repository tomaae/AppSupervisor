using System.Text;
using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>Verifies strict, complete, collision-safe portable profile transfers.</summary>
public sealed class ProfileTransferServiceTests
{
    [Fact]
    public void RoundTrip_ProfileDependency_RequiresExplicitReselection()
    {
        SupervisorProfileConfig source = CreateCompleteProfile();
        source.DependencyProfileId = "external-profile";

        ProfileExportResult exported = ProfileTransferService.Serialize(source);
        ProfileTransferDocument document = System.Text.Json.JsonSerializer.Deserialize<ProfileTransferDocument>(
            exported.Json,
            ConfigJson.CreateOptions()
        )!;
        ProfileImportResult imported = ProfileTransferService.Deserialize(exported.Json, []);

        Assert.True(document.RequiresProfileDependencySelection);
        Assert.Equal("", document.Profile!.DependencyProfileId);
        Assert.Equal("", imported.Profile.DependencyProfileId);
        Assert.Contains(ProfilePortabilityAnalyzer.ProfileDependencyWarning, exported.Warnings);
        Assert.Contains(ProfilePortabilityAnalyzer.ProfileDependencyWarning, imported.Warnings);
    }

    [Fact]
    public void RoundTrip_CompleteProfile_PreservesAllProfileOwnedConfiguration()
    {
        SupervisorProfileConfig source = CreateCompleteProfile();

        ProfileExportResult exported = ProfileTransferService.Serialize(source);
        ProfileImportResult imported = ProfileTransferService.Deserialize(exported.Json, []);

        Assert.DoesNotContain("integrations", exported.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source-secret", exported.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, exported.Warnings.Count);
        Assert.False(imported.Profile.Enabled);
        Assert.NotEqual(source.ProfileId, imported.Profile.ProfileId);
        Assert.Equal(source.Name, imported.Profile.Name);
        Assert.False(imported.NameChanged);
        AssertEquivalentIgnoringImportedIdentities(source, imported.Profile);
    }

    [Fact]
    public void Deserialize_DuplicateNameAndInternalIds_CreatesCollisionFreeProfile()
    {
        SupervisorProfileConfig source = CreateCompleteProfile();
        ProfileExportResult exported = ProfileTransferService.Serialize(source);
        SupervisorProfileConfig existing = ConfigJson.Clone(source);

        ProfileImportResult first = ProfileTransferService.Deserialize(exported.Json, [existing]);
        ProfileImportResult second = ProfileTransferService.Deserialize(
            exported.Json,
            [existing, first.Profile]
        );

        Assert.Equal("Portable profile (Imported)", first.Profile.Name);
        Assert.Equal("Portable profile (Imported 2)", second.Profile.Name);
        Assert.True(first.NameChanged);
        Assert.True(second.NameChanged);
        Assert.False(first.Profile.Enabled);

        HashSet<string> existingIds = [
            existing.ProfileId,
            .. ProfileTransferService.EnumerateResources(existing).Select(resource => resource.ResourceId)
        ];
        string[] importedIds = [
            first.Profile.ProfileId,
            .. ProfileTransferService.EnumerateResources(first.Profile).Select(resource => resource.ResourceId)
        ];
        Assert.Equal(importedIds.Length, importedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(importedIds, id => Assert.DoesNotContain(id, existingIds));

        ManagedResourceConfig[] resources = ProfileTransferService.EnumerateResources(first.Profile)
            .OrderBy(resource => resource.StartupOrder)
            .ToArray();
        Assert.Equal("", resources[0].DependencyResourceId);

        for (int index = 1; index < resources.Length; index++)
            Assert.Equal(resources[index - 1].ResourceId, resources[index].DependencyResourceId);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"format\":\"AppSupervisor.Profile\",\"version\":99,\"portabilityWarnings\":[],\"profile\":{}}")]
    [InlineData("{\"format\":\"AppSupervisor.Profile\",\"version\":1,\"profile\":{}}")]
    [InlineData("{\"format\":\"AppSupervisor.Profile\",\"version\":1,\"portabilityWarnings\":[null],\"profile\":{}}")]
    [InlineData("{\"format\":\"AppSupervisor.Profile\",\"version\":1,\"portabilityWarnings\":[],\"profile\":null}")]
    [InlineData("{\"format\":\"AppSupervisor.Profile\",\"version\":1,\"portabilityWarnings\":[],\"profile\":{},\"integrations\":{\"homeAssistant\":{\"token\":\"secret\"}}}")]
    public void Deserialize_MalformedOrUnsupportedDocument_RejectsInput(string json)
    {
        ProfileTransferException exception = Assert.Throws<ProfileTransferException>(
            () => ProfileTransferService.Deserialize(json, [])
        );

        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void Deserialize_InvalidDependency_RejectsProfileBeforeIdentityRemap()
    {
        ProfileExportResult exported = ProfileTransferService.Serialize(CreateCompleteProfile());
        ProfileTransferDocument document = System.Text.Json.JsonSerializer.Deserialize<ProfileTransferDocument>(
            exported.Json,
            ConfigJson.CreateOptions()
        )!;
        document.Profile!.Services[0].DependencyResourceId = "missing-resource";
        string malformed = System.Text.Json.JsonSerializer.Serialize(
            document,
            ConfigJson.CreateOptions()
        );

        ProfileTransferException exception = Assert.Throws<ProfileTransferException>(
            () => ProfileTransferService.Deserialize(malformed, [])
        );

        Assert.Contains("missing dependencyResourceId", exception.Message);
    }

    [Fact]
    public void SaveAtomic_ThenLoad_RoundTripsWithoutTemporaryFiles()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "profile" + ProfileTransferService.FileSuffix);

        try
        {
            ProfileTransferService.SaveAtomic(path, CreateCompleteProfile());
            ProfileImportResult imported = ProfileTransferService.Load(path, []);

            Assert.True(File.Exists(path));
            Assert.Equal("Portable profile", imported.Profile.Name);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_FileAboveSizeLimit_RejectsBeforeParsing()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "oversized.json");

        try
        {
            File.WriteAllBytes(path, new byte[ProfileTransferService.MaximumImportBytes + 1]);

            ProfileTransferException exception = Assert.Throws<ProfileTransferException>(
                () => ProfileTransferService.Load(path, [])
            );
            Assert.Contains("larger than", exception.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateSuggestedFileName_ReplacesUnsafeCharactersAndUsesPortableSuffix()
    {
        string invalid = "Profile" + new string(Path.GetInvalidFileNameChars()) + ".";

        string fileName = ProfileTransferService.CreateSuggestedFileName(invalid);

        Assert.EndsWith(ProfileTransferService.FileSuffix, fileName, StringComparison.Ordinal);
        Assert.DoesNotContain(fileName, character =>
            Path.GetInvalidFileNameChars().Contains(character));
    }

    private static void AssertEquivalentIgnoringImportedIdentities(
        SupervisorProfileConfig expected,
        SupervisorProfileConfig actual)
    {
        SupervisorProfileConfig comparison = ConfigJson.Clone(actual);
        comparison.ProfileId = expected.ProfileId;
        comparison.Enabled = expected.Enabled;
        comparison.Name = expected.Name;
        ManagedResourceConfig[] expectedResources = ProfileTransferService.EnumerateResources(expected)
            .OrderBy(resource => resource.StartupOrder)
            .ToArray();
        ManagedResourceConfig[] actualResources = ProfileTransferService.EnumerateResources(comparison)
            .OrderBy(resource => resource.StartupOrder)
            .ToArray();
        var importedToSourceIds = actualResources
            .Zip(expectedResources)
            .ToDictionary(pair => pair.First.ResourceId, pair => pair.Second.ResourceId);

        foreach ((ManagedResourceConfig imported, ManagedResourceConfig source) in
            actualResources.Zip(expectedResources))
        {
            imported.ResourceId = source.ResourceId;
            imported.DependencyResourceId = string.IsNullOrWhiteSpace(imported.DependencyResourceId)
                ? ""
                : importedToSourceIds[imported.DependencyResourceId];
        }

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(expected, ConfigJson.CreateOptions()),
            System.Text.Json.JsonSerializer.Serialize(comparison, ConfigJson.CreateOptions())
        );
    }

    private static SupervisorProfileConfig CreateCompleteProfile()
    {
        const string applicationId = "application-id";
        const string serviceId = "service-id";
        const string delayId = "delay-id";
        const string homeAssistantId = "home-assistant-id";
        const string mqttId = "mqtt-id";
        const string obsId = "obs-id";
        const string streamDeckId = "stream-deck-id";
        const string twitchId = "twitch-id";
        const string audioId = "audio-id";

        return new SupervisorProfileConfig
        {
            ProfileId = "profile-id",
            Name = "Portable profile",
            Enabled = true,
            MonitorProcess = "VRChat.exe",
            CloseTimeoutSeconds = 12,
            RestartTimeoutSeconds = 34,
            Applications =
            [
                new ManagedApplicationConfig
                {
                    ResourceId = applicationId,
                    StartupOrder = 0,
                    Path = @"C:\SourceComputer\Apps\Helper.exe",
                    AppUri = @"shell:AppsFolder\Sample.Package_123!App",
                    PackageFamilyName = "Sample.Package_123",
                    PackageApplicationId = "App",
                    PackageExecutable = @"bin\Helper.exe",
                    Restart = false,
                    EnsureClosedUntilNeeded = true,
                    MinimizeAfterStart = true,
                    ForceKillAfterCloseFailure = true,
                    MonitorResponsiveness = true,
                    HealthChecks =
                    [
                        new HealthCheckConfig
                        {
                            Name = "Local listener",
                            Type = HealthCheckType.Listener,
                            Protocol = ListenerProtocol.Tcp,
                            Port = 4567,
                            ActiveWhenProcess = "Gate.exe",
                            IntervalSeconds = 12,
                            TimeoutSeconds = 4,
                            FailureThreshold = 2,
                            StartupDelaySeconds = 8,
                            RestartOnFailure = false
                        }
                    ],
                    StartupMacros =
                    [
                        new StartupMacroActionConfig
                        {
                            Type = StartupMacroActionType.MoveWindow,
                            Monitor = @"\\.\DISPLAY2",
                            X = 120,
                            Y = 80
                        },
                        new StartupMacroActionConfig
                        {
                            Type = StartupMacroActionType.Hotkey,
                            Keys = ["ControlKey", "L"],
                            Monitor = ""
                        }
                    ]
                }
            ],
            Services =
            [
                new ManagedServiceConfig
                {
                    ResourceId = serviceId,
                    StartupOrder = 1,
                    DependencyResourceId = applicationId,
                    ServiceName = "SourceComputerService",
                    Restart = false
                }
            ],
            Delays =
            [
                new DelayResourceConfig
                {
                    ResourceId = delayId,
                    StartupOrder = 2,
                    DependencyResourceId = serviceId,
                    DurationMilliseconds = 2500
                }
            ],
            HomeAssistantResources =
            [
                new HomeAssistantResourceConfig
                {
                    ResourceId = homeAssistantId,
                    StartupOrder = 3,
                    DependencyResourceId = delayId,
                    Service = "switch.turn_on",
                    EntityId = "switch.source_computer",
                    EntityName = "Source computer",
                    VerifyStateChange = true,
                    Persistent = true
                }
            ],
            MqttResources =
            [
                new MqttResourceConfig
                {
                    ResourceId = mqttId,
                    StartupOrder = 4,
                    DependencyResourceId = homeAssistantId,
                    Topic = "portable/device/set",
                    Payload = "ON",
                    VerificationTopic = "portable/device/state",
                    DeactivationBehavior =
                        MqttDeactivationBehavior.PublishConfiguredPayload,
                    DeactivationTopic = "portable/device/set",
                    DeactivationPayload = "OFF",
                    DeactivationRetain = true
                }
            ],
            ObsResources =
            [
                new ObsResourceConfig
                {
                    ResourceId = obsId,
                    StartupOrder = 5,
                    DependencyResourceId = mqttId,
                    Action = ObsActionType.SwitchScene,
                    SceneName = "VR scene"
                }
            ],
            StreamDeckResources =
            [
                new StreamDeckResourceConfig
                {
                    ResourceId = streamDeckId,
                    StartupOrder = 6,
                    DependencyResourceId = obsId,
                    ActionId = "source-computer-action-id",
                    ActionName = "VR toggle",
                    ActionTitle = "Go",
                    IsSwitch = true,
                    RestoreSwitchOnDeactivate = true
                }
            ],
            TwitchResources =
            [
                new TwitchResourceConfig
                {
                    ResourceId = twitchId,
                    StartupOrder = 7,
                    DependencyResourceId = streamDeckId,
                    Action = TwitchActionType.SendChatMessage,
                    Message = "Stream started"
                }
            ],
            AudioInterfaces =
            [
                new AudioInterfaceResourceConfig
                {
                    ResourceId = audioId,
                    StartupOrder = 8,
                    DependencyResourceId = twitchId,
                    EndpointId = "source-endpoint-id",
                    DeviceInstanceId = "source-device-instance",
                    ContainerId = "source-container-id",
                    FriendlyName = "Source headphones",
                    InterfaceName = "USB audio",
                    Direction = AudioInterfaceDirection.Output,
                    VolumePercent = 42,
                    Muted = true,
                    RestoreOnDeactivate = false
                }
            ]
        };
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.ProfileTransferTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }
}
