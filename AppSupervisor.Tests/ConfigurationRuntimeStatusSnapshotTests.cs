using AppSupervisor.Configuration;
using AppSupervisor.Core;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies configuration status snapshots include only applications and services.</summary>
public sealed class ConfigurationRuntimeStatusSnapshotTests
{
    [Fact]
    public void Create_DisabledAndNonRuntimeResources_UsesUnknownOnlyForApplicableRows()
    {
        var application = new ManagedApplicationConfig
        {
            ResourceId = "resource-0",
            Path = "helper.exe"
        };
        var service = new ManagedServiceConfig
        {
            ResourceId = "service-id",
            ServiceName = "HelperService",
            Enabled = false
        };
        var homeAssistant = new HomeAssistantResourceConfig
        {
            ResourceId = "home-assistant-id",
            EntityId = "light.desk"
        };
        var profile = new SupervisorProfileConfig
        {
            ProfileId = "profile-id",
            Name = "Status profile",
            MonitorProcess = "notepad.exe",
            Applications = [application],
            Services = [service],
            HomeAssistantResources = [homeAssistant]
        };
        var configuration = new AppSupervisorConfig { Profiles = [profile] };
        using var runtimeApplication = new ManagedApplication(
            application,
            TimeSpan.Zero,
            shouldRemainRunning: null,
            processIdProvider: () => new HashSet<int> { 42 }
        );
        Assert.True(runtimeApplication.IsRunning());
        using var runtimeProfile = new SupervisorProfile(
            profile.Name,
            profile.MonitorProcess,
            new StubTrigger(),
            [runtimeApplication],
            TimeSpan.Zero
        );

        ConfigurationRuntimeStatusSnapshot snapshot =
            ConfigurationRuntimeStatusSnapshotFactory.Create(configuration, [runtimeProfile]);

        Assert.Equal(2, snapshot.Resources.Count);
        Assert.Equal(
            ConfigurationResourceRuntimeStatus.Running,
            snapshot.GetStatus(profile.ProfileId, application.ResourceId)
        );
        Assert.Equal(
            ConfigurationResourceRuntimeStatus.Unknown,
            snapshot.GetStatus(profile.ProfileId, service.ResourceId)
        );
        Assert.False(snapshot.Resources.ContainsKey(
            new ConfigurationResourceRuntimeStatusKey(
                profile.ProfileId,
                homeAssistant.ResourceId
            )
        ));

        var sameStatuses = new ConfigurationRuntimeStatusSnapshot(
            snapshot.Resources.ToDictionary(entry => entry.Key, entry => entry.Value)
        );
        Assert.True(snapshot.HasSameStatuses(sameStatuses));

        var changedStatuses = new ConfigurationRuntimeStatusSnapshot(
            snapshot.Resources.ToDictionary(
                entry => entry.Key,
                entry => entry.Key.ResourceId == application.ResourceId
                    ? ConfigurationResourceRuntimeStatus.Stopping
                    : entry.Value
            )
        );
        Assert.False(snapshot.HasSameStatuses(changedStatuses));
    }

    private sealed class StubTrigger : ITrigger
    {
        public bool IsActive() => false;
    }
}
