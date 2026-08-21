namespace AppSupervisor.Core;

/// <summary>
/// Coordinates one activation trigger and the managed resources governed by that trigger.
/// </summary>
public sealed class SupervisorProfile : IDisposable
{
    private readonly ITrigger _trigger;
    private readonly IReadOnlyList<ManagedResourceStartup> _startupResources;
    private readonly IReadOnlyDictionary<string, ManagedResourceStartup> _startupResourcesById;
    private readonly IReadOnlyList<IManagedResource> _resources;
    private readonly List<IManagedResource> _activatedResources = [];
    private readonly TimeSpan _closeTimeout;

    private DateTime? _triggerMissingSince;
    private DateTime _nextStartupUtc;
    private int _nextStartupIndex;
    private bool _startupPending;
    private bool _deactivationStarted;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// Creates a supervisor profile with fresh runtime and timeout state.
    /// </summary>
    /// <param name="name">The human-readable profile name.</param>
    /// <param name="triggerDisplayName">The configured human-readable trigger name.</param>
    /// <param name="trigger">The condition that determines whether the profile is active.</param>
    /// <param name="resources">The resources supervised while the trigger is active.</param>
    /// <param name="closeTimeout">How long the trigger may remain inactive before resources are closed.</param>
    public SupervisorProfile(
        string name,
        string triggerDisplayName,
        ITrigger trigger,
        IEnumerable<IManagedResource> resources,
        TimeSpan closeTimeout)
        : this(
            name,
            triggerDisplayName,
            trigger,
            resources.Select((resource, index) => new ManagedResourceStartup(
                resource,
                string.Concat("resource-", index),
                waitAfterStartupMilliseconds: 0,
                dependencyResourceId: ""
            )),
            closeTimeout
        )
    {
    }

    /// <summary>Creates a profile with explicit cross-type startup sequencing metadata.</summary>
    /// <param name="name">The human-readable profile name.</param>
    /// <param name="triggerDisplayName">The configured human-readable trigger name.</param>
    /// <param name="trigger">The condition that determines whether the profile is active.</param>
    /// <param name="startupResources">The resources in startup order with delay and dependency settings.</param>
    /// <param name="closeTimeout">How long the trigger may remain inactive before resources are closed.</param>
    internal SupervisorProfile(
        string name,
        string triggerDisplayName,
        ITrigger trigger,
        IEnumerable<ManagedResourceStartup> startupResources,
        TimeSpan closeTimeout)
    {
        Name = name;
        TriggerDisplayName = triggerDisplayName;
        _trigger = trigger;
        _startupResources = startupResources.ToList();
        _startupResourcesById = _startupResources.ToDictionary(
            startup => startup.ResourceId,
            StringComparer.OrdinalIgnoreCase
        );
        _resources = _startupResources
            .Select(startup => startup.Resource)
            .ToList();
        _closeTimeout = closeTimeout;
        foreach (IManagedResource resource in _resources)
        {
            resource.ErrorOccurred += OnResourceError;

            if (resource is IRecoverableResourceErrorSource recoverableSource)
                recoverableSource.ErrorCleared += OnResourceErrorCleared;

            if (resource is IResourceNotificationSource notificationSource)
                notificationSource.NotificationRequested += OnResourceNotification;
        }
    }

    /// <summary>Occurs when ordinary resource supervision starts a replacement.</summary>
    public event Action<SupervisorProfile, IManagedResource>? ResourceRestarted;

    /// <summary>Occurs when ordinary resource supervision reports an unrecoverable operation failure.</summary>
    public event Action<SupervisorProfile, IManagedResource, string>? ErrorOccurred;

    /// <summary>Occurs when a resource clears a previously reported ordinary supervision error.</summary>
    public event Action<SupervisorProfile, IManagedResource>? ErrorCleared;

    /// <summary>Occurs when a resource publishes a check-specific notification and error-state transition.</summary>
    public event Action<SupervisorProfile, IManagedResource, ResourceNotification>?
        ResourceNotificationRequested;

    /// <summary>Gets the human-readable supervisor profile name.</summary>
    public string Name { get; }

    /// <summary>Gets the configured human-readable trigger name.</summary>
    public string TriggerDisplayName { get; }

    /// <summary>Gets whether the supervisor profile's activation trigger is currently present.</summary>
    public bool TriggerActive { get; private set; }

    /// <summary>Finds a runtime resource by its stable profile-local configuration identifier.</summary>
    internal IManagedResource? FindResource(string resourceId) =>
        _startupResourcesById.TryGetValue(resourceId, out ManagedResourceStartup? startup)
            ? startup.Resource
            : null;

    /// <summary>Gets whether startup has activated the supplied resource for the current profile run.</summary>
    internal bool IsResourceActivated(IManagedResource resource) =>
        _activatedResources.Contains(resource);

    /// <summary>
    /// Gets whether this active profile still has resources to issue or is waiting for an
    /// activated resource to report that it has started.
    /// </summary>
    internal bool StartupPending => TriggerActive && _startupPending;

    /// <summary>Gets whether the missing trigger is still within its configured close timeout.</summary>
    internal bool WaitingForCloseTimeout =>
        !TriggerActive && _triggerMissingSince is not null;

    /// <summary>Gets whether one or more activated resources are still completing deactivation.</summary>
    internal bool ResourceDeactivationPending =>
        !TriggerActive && _deactivationStarted;

    /// <summary>Gets whether a resource transition needs an immediate lifecycle pass.</summary>
    internal bool ImmediateLifecycleWorkPending =>
        _resources.Any(resource =>
            resource is IManagedResourceLifecycleWork lifecycle &&
            lifecycle.LifecycleWorkPending);

    /// <summary>Gets the next delayed startup deadline, if startup is only waiting for time.</summary>
    internal DateTime? NextStartupDueUtc =>
        StartupPending &&
        _nextStartupIndex < _startupResources.Count &&
        SupervisorTime.UtcNow < _nextStartupUtc
            ? _nextStartupUtc
            : null;

    /// <summary>Gets whether startup sequencing or a resource transition needs the lifecycle timer.</summary>
    internal bool LifecycleWorkPending =>
        ImmediateLifecycleWorkPending || NextStartupDueUtc is not null;

    /// <summary>Gets whether cancelled profile background work is still unwinding for pause.</summary>
    internal bool PauseDrainPending =>
        _resources.Any(resource =>
            resource is IPauseDrainWork drain && drain.PauseDrainPending);

    /// <summary>
    /// Checks whether this profile currently requires its resources to remain available.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> while the trigger is present, its close timeout is still running,
    /// or trigger state cannot be checked safely.
    /// </returns>
    internal bool KeepsResourcesActive()
    {
        if (_disposed)
            return false;

        if (TriggerActive || (_triggerMissingSince is not null && !_deactivationStarted))
            return true;

        try
        {
            return _trigger.IsActive();
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Performs one-time initialization for every resource after the complete configuration is accepted.
    /// </summary>
    public void InitializeResources()
    {
        if (_disposed)
            return;

        foreach (IManagedResource resource in _resources)
            RunResourceOperation(resource, resource.Initialize, "initialization");
    }

    /// <summary>
    /// Evaluates the trigger and performs one supervision or graceful-deactivation cycle for the profile's resources.
    /// </summary>
    /// <returns><see langword="true"/> when the trigger changed between active and inactive states.</returns>
    public bool Update()
    {
        if (_disposed)
            return false;

        bool isActive = _trigger.IsActive();

        if (!_initialized)
        {
            TriggerActive = isActive;
            _initialized = true;

            if (isActive)
                BeginStartupSequence();

            return false;
        }

        if (isActive)
        {
            _triggerMissingSince = null;
            _deactivationStarted = false;

            if (!TriggerActive)
            {
                TriggerActive = true;
                BeginStartupSequence();
                return true;
            }

            foreach (IManagedResource resource in _activatedResources)
            {
                try
                {
                    if (resource.Supervise() == ManagedResourceUpdate.Restarted)
                        ResourceRestarted?.Invoke(this, resource);
                }
                catch (Exception ex)
                {
                    OnResourceError(resource, $"Unexpected supervision failure: {ex.Message}");
                }
            }

            AdvanceStartup(SupervisorTime.UtcNow);
            return false;
        }

        if (TriggerActive)
        {
            SupervisorLog.WriteTrace(
                $"Profile '{Name}': trigger disappeared; entering close timeout."
            );
            TriggerActive = false;
            _triggerMissingSince = SupervisorTime.UtcNow;
            CancelStartupSequence();
            _deactivationStarted = false;

            foreach (IManagedResource resource in _resources)
            {
                SupervisorLog.WriteTrace(
                    $"Profile '{Name}': cancelling pending recovery for '{resource.DisplayName}'."
                );
                RunResourceOperation(
                    resource,
                    resource.CancelPendingRecovery,
                    "recovery cancellation"
                );
                SupervisorLog.WriteTrace(
                    $"Profile '{Name}': pending recovery cancelled for '{resource.DisplayName}'."
                );
            }

            SupervisorLog.WriteTrace(
                $"Profile '{Name}': trigger-loss transition completed."
            );
            return true;
        }

        if (_deactivationStarted)
        {
            foreach (IManagedResource resource in _activatedResources)
            {
                if (resource is IManagedResourceLifecycleWork)
                    continue;

                RunResourceOperation(
                    resource,
                    resource.SuperviseDeactivation,
                    "deactivation supervision"
                );
            }

            if (!HasPendingResourceDeactivation())
            {
                _deactivationStarted = false;
                _activatedResources.Clear();
            }

            return false;
        }

        if (_triggerMissingSince is null)
            return false;

        if (SupervisorTime.UtcNow - _triggerMissingSince >= _closeTimeout)
        {
            SupervisorLog.WriteTrace(
                $"Profile '{Name}': close timeout elapsed; beginning resource deactivation."
            );
            DeactivateResources();
            _triggerMissingSince = null;
            _deactivationStarted = _activatedResources.Count > 0;
            SupervisorLog.WriteTrace(
                $"Profile '{Name}': resource deactivation requests completed; " +
                $"pending={_deactivationStarted}."
            );
        }

        return false;
    }

    /// <summary>Advances resource transitions first, then dependency and delay-gated startup.</summary>
    /// <param name="nowUtc">The timestamp shared by the serialized lifecycle pass.</param>
    internal void AdvanceLifecycle(DateTime nowUtc)
    {
        if (_disposed)
            return;

        foreach (IManagedResource resource in _resources)
        {
            if (resource is not IManagedResourceLifecycleWork lifecycle ||
                !lifecycle.LifecycleWorkPending)
            {
                continue;
            }

            try
            {
                if (lifecycle.AdvanceLifecycle(nowUtc) == ManagedResourceUpdate.Restarted)
                    ResourceRestarted?.Invoke(this, resource);
            }
            catch (Exception ex)
            {
                OnResourceError(resource, $"Unexpected lifecycle failure: {ex.Message}");
            }
        }

        AdvanceStartup(nowUtc);

        if (_deactivationStarted && !HasPendingResourceDeactivation())
        {
            _deactivationStarted = false;
            _activatedResources.Clear();
        }
    }

    /// <summary>Requests cancellation of background probes after the current monitor pass finishes.</summary>
    internal void BeginPauseDrain()
    {
        foreach (IPauseDrainWork drain in _resources.OfType<IPauseDrainWork>())
            drain.BeginPauseDrain();
    }

    /// <summary>Reaps background probe cancellation from the lifecycle timer.</summary>
    internal void AdvancePauseDrain()
    {
        foreach (IPauseDrainWork drain in _resources.OfType<IPauseDrainWork>())
            drain.AdvancePauseDrain();
    }

    /// <summary>Cancels asynchronous resource monitoring while leaving all external resources untouched.</summary>
    public void SuspendMonitoring()
    {
        if (_disposed)
            return;

        foreach (IManagedResource resource in _resources)
            RunResourceOperation(resource, resource.SuspendMonitoring, "monitoring suspension");
    }

    /// <summary>
    /// Unsubscribes resource events and cancels resource-owned asynchronous work without altering external resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        CancelStartupSequence();
        _activatedResources.Clear();
        _disposed = true;

        foreach (IManagedResource resource in _resources)
        {
            resource.ErrorOccurred -= OnResourceError;

            if (resource is IRecoverableResourceErrorSource recoverableSource)
                recoverableSource.ErrorCleared -= OnResourceErrorCleared;

            if (resource is IResourceNotificationSource notificationSource)
                notificationSource.NotificationRequested -= OnResourceNotification;

            RunResourceOperation(resource, resource.Dispose, "disposal");
        }

        ResourceRestarted = null;
        ErrorOccurred = null;
        ResourceNotificationRequested = null;
        ErrorCleared = null;
    }

    /// <summary>Starts a fresh ordered activation sequence immediately at the first configured resource.</summary>
    private void BeginStartupSequence()
    {
        _activatedResources.Clear();
        _nextStartupIndex = 0;
        DateTime nowUtc = SupervisorTime.UtcNow;
        _nextStartupUtc = nowUtc;
        _startupPending = _startupResources.Count > 0;
        AdvanceStartup(nowUtc);
    }

    /// <summary>Advances timestamp and dependency-gated startup without sleeping or blocking another profile.</summary>
    /// <param name="nowUtc">The current UTC time supplied by the startup timer or profile activation.</param>
    internal void AdvanceStartup(DateTime nowUtc)
    {
        if (_disposed || !TriggerActive || !_startupPending || nowUtc < _nextStartupUtc)
            return;

        while (_nextStartupIndex < _startupResources.Count)
        {
            ManagedResourceStartup startup = _startupResources[_nextStartupIndex];

            if (!string.IsNullOrWhiteSpace(startup.DependencyResourceId) &&
                !IsDependencyStarted(startup.DependencyResourceId))
            {
                return;
            }

            RunResourceOperation(startup.Resource, startup.Resource.Activate, "activation");

            if (!_activatedResources.Contains(startup.Resource))
                _activatedResources.Add(startup.Resource);

            _nextStartupIndex++;
            _nextStartupUtc = nowUtc +
                TimeSpan.FromMilliseconds(startup.WaitAfterStartupMilliseconds);

            if (startup.WaitAfterStartupMilliseconds > 0)
                return;
        }

        _startupPending = !AreActivatedResourcesStarted();
    }

    /// <summary>Checks whether every activated resource with a readiness signal has started.</summary>
    /// <returns><see langword="true"/> when no activated resource is still starting.</returns>
    private bool AreActivatedResourcesStarted()
    {
        foreach (IManagedResource resource in _activatedResources)
        {
            if (resource is not IManagedResourceReadiness readiness)
                continue;

            try
            {
                if (!readiness.IsStarted())
                    return false;
            }
            catch (Exception ex)
            {
                OnResourceError(resource, $"Unexpected startup readiness failure: {ex.Message}");
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks whether an earlier activated resource is ready for a dependent startup entry.</summary>
    /// <param name="dependencyResourceId">The stable profile-local dependency identifier.</param>
    /// <returns><see langword="true"/> when the dependency has been activated and reports started.</returns>
    private bool IsDependencyStarted(string dependencyResourceId)
    {
        if (!_startupResourcesById.TryGetValue(dependencyResourceId, out ManagedResourceStartup? dependency) ||
            !_activatedResources.Contains(dependency.Resource))
        {
            return false;
        }

        if (dependency.Resource is not IManagedResourceReadiness readiness)
            return true;

        try
        {
            return readiness.IsStarted();
        }
        catch (Exception ex)
        {
            OnResourceError(
                dependency.Resource,
                $"Unexpected dependency readiness failure: {ex.Message}"
            );
            return false;
        }
    }

    /// <summary>Cancels unissued startup entries while leaving already activated resources untouched.</summary>
    private void CancelStartupSequence()
    {
        _startupPending = false;
        _nextStartupIndex = 0;
        _nextStartupUtc = DateTime.MinValue;
    }

    /// <summary>Begins gracefully closing resources reached by the current activation sequence.</summary>
    private void DeactivateResources()
    {
        foreach (IManagedResource resource in _activatedResources)
            RunResourceOperation(resource, resource.Deactivate, "deactivation");
    }

    /// <summary>Checks whether any activated resource still reports asynchronous deactivation work.</summary>
    /// <returns><see langword="true"/> while at least one resource still needs deactivation supervision.</returns>
    private bool HasPendingResourceDeactivation()
    {
        return _activatedResources.Any(resource =>
            resource is IManagedResourceDeactivationState state &&
            state.DeactivationPending
        );
    }

    /// <summary>
    /// Runs one resource lifecycle operation while isolating unexpected failures from other resources.
    /// </summary>
    /// <param name="resource">The resource that owns the operation.</param>
    /// <param name="operation">The lifecycle operation to execute.</param>
    /// <param name="operationName">The user-readable operation name included in failure reports.</param>
    private void RunResourceOperation(
        IManagedResource resource,
        Action operation,
        string operationName)
    {
        try
        {
            operation();
        }
        catch (Exception ex)
        {
            OnResourceError(resource, $"Unexpected {operationName} failure: {ex.Message}");
        }
    }

    /// <summary>Forwards a managed-resource failure to the supervisor profile's notification subscriber.</summary>
    /// <param name="resource">The resource that reported the failure.</param>
    /// <param name="message">The user-readable failure message.</param>
    private void OnResourceError(IManagedResource resource, string message)
    {
        ErrorOccurred?.Invoke(this, resource, message);
    }

    /// <summary>Forwards resource recovery to the tray error-state tracker.</summary>
    /// <param name="resource">The managed resource that recovered.</param>
    private void OnResourceErrorCleared(IManagedResource resource)
    {
        ErrorCleared?.Invoke(this, resource);
    }

    /// <summary>Forwards a resource-specific notification without replacing its targets or error-state semantics.</summary>
    /// <param name="resource">The resource that owns the notification.</param>
    /// <param name="notification">The check-specific notification payload.</param>
    private void OnResourceNotification(
        IManagedResource resource,
        ResourceNotification notification)
    {
        ResourceNotificationRequested?.Invoke(this, resource, notification);
    }
}
