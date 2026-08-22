using AppSupervisor.Configuration;
using AppSupervisor.Core;
using AppSupervisor.Notifications;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies global shared-helper ownership and the nonblocking inactive-application cleanup lifecycle.
/// </summary>
public sealed class ApplicationUsageRegistryTests
{
    /// <summary>Confirms an active profile protects the same executable from another profile's deactivation.</summary>
    [Fact]
    public void IsRequiredByAnotherProfile_SharedActiveOwner_ProtectsHelper()
    {
        string path = Path.Combine(Path.GetTempPath(), "SharedHelper.exe");
        using var registry = new ApplicationUsageRegistry(_ => new FakeApplicationLifecycle());
        var inactiveOwner = new object();
        var activeOwner = new object();
        bool inactiveNeedsResources = false;
        bool activeNeedsResources = true;

        registry.RegisterApplication(
            new ManagedApplicationConfig { Path = path },
            inactiveOwner,
            () => inactiveNeedsResources
        );
        registry.RegisterApplication(
            new ManagedApplicationConfig { Path = path.ToUpperInvariant() },
            activeOwner,
            () => activeNeedsResources
        );
        registry.CompleteRegistration();

        Assert.True(registry.IsRequiredByAnotherProfile(path, inactiveOwner));
        Assert.False(registry.IsRequiredByAnotherProfile(path, activeOwner));
        Assert.True(registry.IsRequiredByAnyActiveProfile(path));

        inactiveNeedsResources = true;

        Assert.True(registry.IsRequiredByAnotherProfile(path, activeOwner));

        activeNeedsResources = false;
        inactiveNeedsResources = false;

        Assert.False(registry.IsRequiredByAnyActiveProfile(path));
    }

    /// <summary>Confirms cleanup starts only while unused and is cancelled when any profile needs the helper again.</summary>
    [Fact]
    public void Sweep_OptedInSharedHelper_RechecksUsageDuringClose()
    {
        string path = Path.Combine(Path.GetTempPath(), "SweepHelper.exe");
        FakeApplicationLifecycle? cleanup = null;
        ManagedApplicationConfig? cleanupConfiguration = null;
        using var registry = new ApplicationUsageRegistry(configuration =>
        {
            cleanupConfiguration = configuration;
            cleanup = new FakeApplicationLifecycle
            {
                Config = configuration
            };
            return cleanup;
        });
        var ensuringOwner = new object();
        var sharedOwner = new object();
        bool ensuringActive = false;
        bool sharedActive = true;

        registry.RegisterApplication(
            new ManagedApplicationConfig
            {
                Path = path,
                EnsureClosedUntilNeeded = true,
                Notifications = new NotificationConfig
                {
                    Target = [NotificationTarget.Popup]
                }
            },
            ensuringOwner,
            () => ensuringActive
        );
        registry.RegisterApplication(
            new ManagedApplicationConfig
            {
                Path = path,
                EnsureClosedUntilNeeded = false
            },
            sharedOwner,
            () => sharedActive
        );
        registry.CompleteRegistration();

        Assert.True(registry.HasCleanupTargets);
        Assert.NotNull(cleanup);
        Assert.NotNull(cleanupConfiguration);
        Assert.False(cleanupConfiguration.Restart);
        Assert.Equal([NotificationTarget.Popup], cleanupConfiguration.Notifications.Target);

        registry.Sweep();

        Assert.Equal(0, cleanup.DeactivateCalls);

        sharedActive = false;
        registry.Sweep();

        Assert.Equal(1, cleanup.DeactivateCalls);
        Assert.True(cleanup.CloseOperationPending);

        ensuringActive = true;
        registry.AdvanceCleanup();

        Assert.True(cleanup.CloseOperationPending);
        Assert.Equal(0, cleanup.CancelPendingRecoveryCalls);
        Assert.Equal(1, cleanup.SuperviseDeactivationCalls);

        ensuringActive = false;
        registry.Sweep();
        registry.AdvanceCleanup();

        Assert.Equal(1, cleanup.DeactivateCalls);
        Assert.Equal(2, cleanup.SuperviseDeactivationCalls);
        Assert.True(cleanup.CloseOperationPending);

        registry.SuspendCleanup();

        Assert.False(cleanup.CloseOperationPending);
    }

    /// <summary>Confirms no cleanup lifecycle is created when every shared reference leaves the option disabled.</summary>
    [Fact]
    public void CompleteRegistration_NoEnsureClosedReference_DoesNotCreateCleaner()
    {
        int cleanupFactoryCalls = 0;
        using var registry = new ApplicationUsageRegistry(configuration =>
        {
            cleanupFactoryCalls++;
            return new FakeApplicationLifecycle
            {
                Config = configuration
            };
        });

        registry.RegisterApplication(
            new ManagedApplicationConfig
            {
                Path = Path.Combine(Path.GetTempPath(), "OrdinaryHelper.exe")
            },
            new object(),
            () => false
        );
        registry.CompleteRegistration();
        registry.Sweep();

        Assert.False(registry.HasCleanupTargets);
        Assert.Equal(0, cleanupFactoryCalls);
    }

    /// <summary>Confirms cleanup settings never cross executable-path ownership boundaries.</summary>
    [Fact]
    public void CompleteRegistration_DifferentHelpers_KeepCleanupSettingsIsolated()
    {
        string firstPath = Path.Combine(Path.GetTempPath(), "FirstCleanupHelper.exe");
        string secondPath = Path.Combine(Path.GetTempPath(), "SecondCleanupHelper.exe");
        var cleanupConfigurations = new List<ManagedApplicationConfig>();
        using var registry = new ApplicationUsageRegistry(configuration =>
        {
            cleanupConfigurations.Add(configuration);
            return new FakeApplicationLifecycle { Config = configuration };
        });

        registry.RegisterApplication(
            new ManagedApplicationConfig
            {
                Path = firstPath,
                EnsureClosedUntilNeeded = true,
                ForceKillAfterCloseFailure = true,
                Notifications = new NotificationConfig
                {
                    Target = [NotificationTarget.Popup]
                }
            },
            new object(),
            () => false
        );
        registry.RegisterApplication(
            new ManagedApplicationConfig
            {
                Path = secondPath,
                EnsureClosedUntilNeeded = true,
                ForceKillAfterCloseFailure = false,
                Notifications = new NotificationConfig
                {
                    Target = [NotificationTarget.Windows]
                }
            },
            new object(),
            () => false
        );

        registry.CompleteRegistration();

        ManagedApplicationConfig first = Assert.Single(cleanupConfigurations,
            configuration => string.Equals(
                configuration.Path,
                firstPath,
                StringComparison.OrdinalIgnoreCase
            ));
        ManagedApplicationConfig second = Assert.Single(cleanupConfigurations,
            configuration => string.Equals(
                configuration.Path,
                secondPath,
                StringComparison.OrdinalIgnoreCase
            ));
        Assert.True(first.ForceKillAfterCloseFailure);
        Assert.Equal([NotificationTarget.Popup], first.Notifications.Target);
        Assert.False(second.ForceKillAfterCloseFailure);
        Assert.Equal([NotificationTarget.Windows], second.Notifications.Target);
    }

    /// <summary>Confirms leave-running takes precedence over the contradictory inactive-cleanup option.</summary>
    [Fact]
    public void CompleteRegistration_LeaveRunningReference_DoesNotCreateCleaner()
    {
        int cleanupFactoryCalls = 0;
        using var registry = new ApplicationUsageRegistry(configuration =>
        {
            cleanupFactoryCalls++;
            return new FakeApplicationLifecycle
            {
                Config = configuration
            };
        });

        registry.RegisterApplication(
            new ManagedApplicationConfig
            {
                Path = Path.Combine(Path.GetTempPath(), "PersistentHelper.exe"),
                EnsureClosedUntilNeeded = true,
                LeaveRunningAfterProfileStops = true
            },
            new object(),
            () => false
        );
        registry.CompleteRegistration();
        registry.Sweep();

        Assert.False(registry.HasCleanupTargets);
        Assert.Equal(0, cleanupFactoryCalls);
    }

    /// <summary>Confirms one persistent shared reference protects the helper from another owner's cleanup.</summary>
    [Fact]
    public void Sweep_SharedLeaveRunningReference_ProtectsHelperWhileInactive()
    {
        string path = Path.Combine(Path.GetTempPath(), "SharedPersistentHelper.exe");
        FakeApplicationLifecycle? cleanup = null;
        using var registry = new ApplicationUsageRegistry(configuration =>
        {
            cleanup = new FakeApplicationLifecycle
            {
                Config = configuration
            };
            return cleanup;
        });
        var ensuringOwner = new object();
        var persistentOwner = new object();

        registry.RegisterApplication(
            new ManagedApplicationConfig
            {
                Path = path,
                EnsureClosedUntilNeeded = true
            },
            ensuringOwner,
            () => false
        );
        registry.RegisterApplication(
            new ManagedApplicationConfig
            {
                Path = path,
                LeaveRunningAfterProfileStops = true
            },
            persistentOwner,
            () => false
        );
        registry.CompleteRegistration();

        Assert.NotNull(cleanup);
        Assert.True(registry.IsRequiredByAnotherProfile(path, ensuringOwner));

        registry.Sweep();

        Assert.Equal(0, cleanup.DeactivateCalls);
    }

    /// <summary>Confirms the same executable may be intentionally shared by two enabled profiles.</summary>
    [Fact]
    public void Validate_SharedExecutableAcrossProfiles_Succeeds()
    {
        string executablePath = Environment.ProcessPath!;

        ConfigValidator.Validate(
        [
            CreateProfile("First", "FirstMonitor.exe", executablePath),
            CreateProfile("Second", "SecondMonitor.exe", executablePath)
        ]);
    }

    /// <summary>Confirms accidental duplicate executable entries remain invalid inside one profile.</summary>
    [Fact]
    public void Validate_DuplicateExecutableWithinProfile_Fails()
    {
        string executablePath = Environment.ProcessPath!;
        SupervisorProfileConfig profile = CreateProfile(
            "Duplicate",
            "Monitor.exe",
            executablePath
        );
        profile.Applications.Add(new ManagedApplicationConfig
        {
            Path = executablePath
        });

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("duplicates the helper path", exception.Message);
    }

    /// <summary>Creates one valid profile containing a single enabled helper application.</summary>
    /// <param name="name">The unique profile name.</param>
    /// <param name="monitorProcess">The profile trigger process name.</param>
    /// <param name="executablePath">The shared helper executable path.</param>
    /// <returns>A validation-ready profile configuration.</returns>
    private static SupervisorProfileConfig CreateProfile(
        string name,
        string monitorProcess,
        string executablePath)
    {
        return new SupervisorProfileConfig
        {
            Name = name,
            MonitorProcess = monitorProcess,
            Applications =
            [
                new ManagedApplicationConfig
                {
                    Path = executablePath
                }
            ]
        };
    }

    /// <summary>Records close lifecycle calls without interacting with real processes.</summary>
    private sealed class FakeApplicationLifecycle : IManagedApplicationLifecycle
    {
        /// <summary>Provides a no-op error event for the lifecycle contract.</summary>
        public event Action<IManagedResource, string>? ErrorOccurred
        {
            add { }
            remove { }
        }

        /// <summary>Gets or sets the cleanup configuration supplied by the registry.</summary>
        public ManagedApplicationConfig Config { get; set; } = new();

        /// <summary>Gets the fake helper name.</summary>
        public string DisplayName => "Fake helper";

        /// <summary>Gets the configured cleanup notification targets.</summary>
        public IReadOnlyList<NotificationTarget> NotificationTargets =>
            Config.Notifications.Target;

        /// <summary>Gets whether a simulated close operation remains pending.</summary>
        public bool CloseOperationPending { get; private set; }

        /// <summary>Gets the number of deactivation requests.</summary>
        public int DeactivateCalls { get; private set; }

        /// <summary>Gets the number of pending-work cancellations.</summary>
        public int CancelPendingRecoveryCalls { get; private set; }

        /// <summary>Gets the number of close-progression calls.</summary>
        public int SuperviseDeactivationCalls { get; private set; }

        /// <summary>Performs no activation work.</summary>
        public void Activate() { }

        /// <summary>Returns whether the simulated helper is currently running.</summary>
        /// <returns><see langword="false"/> for this close-only fake.</returns>
        public bool IsRunning() => false;

        /// <summary>Returns no active-supervision transition.</summary>
        /// <returns><see cref="ManagedResourceUpdate.None"/>.</returns>
        public ManagedResourceUpdate Supervise() => ManagedResourceUpdate.None;

        /// <summary>Cancels the simulated close operation.</summary>
        public void CancelPendingRecovery()
        {
            CancelPendingRecoveryCalls++;
            CloseOperationPending = false;
        }

        /// <summary>Performs no monitoring-suspension work.</summary>
        public void SuspendMonitoring() { }

        /// <summary>Starts one simulated nonblocking close operation.</summary>
        public void Deactivate()
        {
            DeactivateCalls++;
            CloseOperationPending = true;
        }

        /// <summary>Records one simulated close-progression cycle.</summary>
        public void SuperviseDeactivation()
        {
            SuperviseDeactivationCalls++;
        }

        /// <summary>Releases no external resources.</summary>
        public void Dispose() { }
    }
}
