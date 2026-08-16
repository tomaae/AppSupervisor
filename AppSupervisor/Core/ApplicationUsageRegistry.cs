using AppSupervisor.Notifications;
using AppSupervisor.Resources;

namespace AppSupervisor.Core;

/// <summary>
/// Indexes enabled helper applications across profiles, protects shared active usage, and owns periodic inactive-helper closing.
/// </summary>
internal sealed class ApplicationUsageRegistry : IDisposable
{
    private readonly Func<ManagedApplicationConfig, IManagedApplicationLifecycle> _cleanupFactory;
    private readonly Dictionary<string, ApplicationUsage> _usages =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _registrationCompleted;
    private bool _disposed;

    /// <summary>Gets whether registration produced at least one inactive-helper cleanup lifecycle.</summary>
    public bool HasCleanupTargets { get; private set; }

    /// <summary>Gets whether an inactive cleanup owns lifecycle work that must be drained.</summary>
    public bool LifecycleWorkPending =>
        !_disposed && _usages.Values.Any(usage => usage.LifecycleWorkPending);

    /// <summary>Occurs when an inactive helper remains open after all configured close attempts.</summary>
    public event Action<string, string, IReadOnlyList<NotificationTarget>>? CleanupFailed;

    /// <summary>Occurs when a later cleanup sweep confirms recovery from the last close failure.</summary>
    public event Action? CleanupRecovered;

    /// <summary>Creates a registry that uses the production managed-application close lifecycle.</summary>
    public ApplicationUsageRegistry()
        : this(configuration => new ManagedApplication(configuration, TimeSpan.Zero))
    {
    }

    /// <summary>Creates a registry with an injectable cleanup lifecycle for focused testing.</summary>
    /// <param name="cleanupFactory">Creates the close-only lifecycle for an opted-in executable.</param>
    internal ApplicationUsageRegistry(
        Func<ManagedApplicationConfig, IManagedApplicationLifecycle> cleanupFactory)
    {
        _cleanupFactory = cleanupFactory;
    }

    /// <summary>Registers every enabled application belonging to one enabled runtime profile.</summary>
    /// <param name="configuration">The accepted configuration used to build the runtime profile.</param>
    /// <param name="profile">The runtime profile that reports whether its resources remain needed.</param>
    public void RegisterProfile(
        SupervisorProfileConfig configuration,
        SupervisorProfile profile)
    {
        foreach (ManagedApplicationConfig application in
            configuration.Applications.Where(application => application.Enabled))
        {
            RegisterApplication(
                application,
                profile,
                profile.KeepsResourcesActive
            );
        }
    }

    /// <summary>Registers one application reference with an injectable owner state for focused lifecycle testing.</summary>
    /// <param name="configuration">The application configuration and executable identity.</param>
    /// <param name="owner">The unique profile owner used when excluding the closing profile.</param>
    /// <param name="keepsResourcesActive">Reports whether this profile still requires its resources.</param>
    internal void RegisterApplication(
        ManagedApplicationConfig configuration,
        object owner,
        Func<bool> keepsResourcesActive)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registrationCompleted)
            throw new InvalidOperationException("Application usage registration is already complete.");

        string key = NormalizePath(configuration.Path);

        if (!_usages.TryGetValue(key, out ApplicationUsage? usage))
        {
            usage = new ApplicationUsage(key, ReportCleanupFailure, ReportCleanupRecovery);
            _usages.Add(key, usage);
        }

        usage.AddReference(configuration, owner, keepsResourcesActive);
    }

    /// <summary>Creates cleanup lifecycle instances after every profile has been registered.</summary>
    public void CompleteRegistration()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registrationCompleted)
            return;

        foreach (ApplicationUsage usage in _usages.Values)
            usage.CompleteRegistration(_cleanupFactory);

        HasCleanupTargets = _usages.Values.Any(usage => usage.CleanupEnabled);
        _registrationCompleted = true;
    }

    /// <summary>Checks whether any profile other than the requester still needs the executable.</summary>
    /// <param name="executablePath">The helper executable identity.</param>
    /// <param name="requestingOwner">The profile currently attempting normal deactivation.</param>
    /// <returns><see langword="true"/> when another enabled profile must keep the helper running.</returns>
    public bool IsRequiredByAnotherProfile(
        string executablePath,
        object requestingOwner)
    {
        if (_disposed)
            return true;

        return _usages.TryGetValue(
            NormalizePath(executablePath),
            out ApplicationUsage? usage
        ) && usage.IsRequiredByAnotherOwner(requestingOwner);
    }

    /// <summary>
    /// Starts nonblocking close operations for opted-in helpers that are not needed by any profile.
    /// </summary>
    public void Sweep()
    {
        if (_disposed || !_registrationCompleted)
            return;

        foreach (ApplicationUsage usage in _usages.Values)
            usage.BeginCleanupIfUnused();
    }

    /// <summary>
    /// Advances already-started graceful close operations to a terminal state.
    /// </summary>
    public void AdvanceCleanup()
    {
        AdvanceLifecycle(SupervisorTime.UtcNow);
    }

    /// <summary>Advances pending cleanup transitions from the shared lifecycle timer.</summary>
    public void AdvanceLifecycle(DateTime nowUtc)
    {
        if (_disposed || !_registrationCompleted)
            return;

        foreach (ApplicationUsage usage in _usages.Values)
            usage.AdvanceCleanup(nowUtc);
    }

    /// <summary>Cancels every pending cleanup close so paused time cannot advance a fallback timeout.</summary>
    public void SuspendCleanup()
    {
        if (_disposed || !_registrationCompleted)
            return;

        foreach (ApplicationUsage usage in _usages.Values)
            usage.CancelCleanup();
    }

    /// <summary>Cancels cleanup work and releases lifecycle objects without touching external processes.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (ApplicationUsage usage in _usages.Values)
            usage.Dispose();

        HasCleanupTargets = false;
        _usages.Clear();
        CleanupFailed = null;
        CleanupRecovered = null;
    }

    /// <summary>Normalizes executable paths into the case-insensitive identity used by the global index.</summary>
    /// <param name="path">The validated fully qualified executable path.</param>
    /// <returns>The canonical full path.</returns>
    private static string NormalizePath(string path) => Path.GetFullPath(path);

    /// <summary>Forwards a cleanup close failure with the merged notification targets for that executable.</summary>
    /// <param name="displayName">The helper filename displayed to the user.</param>
    /// <param name="message">The close failure reported by the managed application lifecycle.</param>
    /// <param name="targets">The combined targets from entries that enabled inactive closing.</param>
    private void ReportCleanupFailure(
        string displayName,
        string message,
        IReadOnlyList<NotificationTarget> targets)
    {
        CleanupFailed?.Invoke(displayName, message, targets);
    }

    /// <summary>Forwards cleanup recovery after a later successful inactive-helper sweep.</summary>
    private void ReportCleanupRecovery()
    {
        CleanupRecovered?.Invoke();
    }

    /// <summary>Tracks every enabled profile reference and one optional cleanup lifecycle for an executable.</summary>
    private sealed class ApplicationUsage : IDisposable
    {
        private readonly string _path;
        private readonly Action<string, string, IReadOnlyList<NotificationTarget>> _reportFailure;
        private readonly List<ApplicationReference> _references = [];

        private readonly Action _reportRecovery;
        private IManagedApplicationLifecycle? _cleanupApplication;
        private IReadOnlyList<NotificationTarget> _cleanupTargets = [];

        /// <summary>Gets whether this executable owns an effective inactive-cleanup lifecycle.</summary>
        public bool CleanupEnabled => _cleanupApplication is not null;

        /// <summary>Gets whether this cleanup application owns transition work.</summary>
        public bool LifecycleWorkPending =>
            _cleanupApplication is IManagedResourceLifecycleWork lifecycle &&
            lifecycle.LifecycleWorkPending;

        /// <summary>Creates an empty executable usage group.</summary>
        /// <param name="path">The canonical executable path.</param>
        /// <param name="reportFailure">Receives completed cleanup failures.</param>
        /// <param name="reportRecovery">Receives recovery after a later successful cleanup sweep.</param>
        public ApplicationUsage(
            string path,
            Action<string, string, IReadOnlyList<NotificationTarget>> reportFailure,
            Action reportRecovery)
        {
            _path = path;
            _reportFailure = reportFailure;
            _reportRecovery = reportRecovery;
        }

        /// <summary>Adds one enabled profile reference to this executable.</summary>
        /// <param name="configuration">The profile-specific application settings.</param>
        /// <param name="owner">The unique profile owner.</param>
        /// <param name="keepsResourcesActive">Reports whether the owner still needs its resources.</param>
        public void AddReference(
            ManagedApplicationConfig configuration,
            object owner,
            Func<bool> keepsResourcesActive)
        {
            _references.Add(new ApplicationReference(
                configuration,
                owner,
                keepsResourcesActive
            ));
        }

        /// <summary>Builds the cleaner only when at least one reference enabled inactive closing.</summary>
        /// <param name="cleanupFactory">Creates the close-only lifecycle for this executable.</param>
        public void CompleteRegistration(
            Func<ManagedApplicationConfig, IManagedApplicationLifecycle> cleanupFactory)
        {
            ApplicationReference[] cleanupReferences = _references
                .Where(reference =>
                    reference.Configuration.EnsureClosedUntilNeeded &&
                    !reference.Configuration.LeaveRunningAfterProfileStops)
                .ToArray();

            if (cleanupReferences.Length == 0)
                return;

            _cleanupTargets = cleanupReferences
                .SelectMany(reference => reference.Configuration.Notifications.Target)
                .Distinct()
                .ToArray();

            var cleanupConfiguration = new ManagedApplicationConfig
            {
                Path = _path,
                Restart = false,
                ForceKillAfterCloseFailure = cleanupReferences.Any(
                    reference => reference.Configuration.ForceKillAfterCloseFailure
                ),
                Notifications = new NotificationConfig
                {
                    Target = [.. _cleanupTargets]
                }
            };

            _cleanupApplication = cleanupFactory(cleanupConfiguration);
            _cleanupApplication.ErrorOccurred += CleanupApplicationFailed;

            if (_cleanupApplication is IRecoverableResourceErrorSource recoverableSource)
                recoverableSource.ErrorCleared += CleanupApplicationRecovered;
        }

        /// <summary>Checks whether a different profile still requires this executable.</summary>
        /// <param name="requestingOwner">The owner whose deactivation is being evaluated.</param>
        /// <returns><see langword="true"/> when any other reference reports that resources remain needed.</returns>
        public bool IsRequiredByAnotherOwner(object requestingOwner)
        {
            return _references.Any(reference =>
                !ReferenceEquals(reference.Owner, requestingOwner) &&
                reference.RequiresHelperToRemainRunning());
        }

        /// <summary>Starts a graceful cleanup only when no profile currently needs the helper.</summary>
        public void BeginCleanupIfUnused()
        {
            if (_cleanupApplication is null)
                return;

            if (IsRequiredByAnyOwner())
                return;

            if (!_cleanupApplication.CloseOperationPending)
                _cleanupApplication.Deactivate();
        }

        /// <summary>Advances one accepted cleanup close without interrupting it midway.</summary>
        public void AdvanceCleanup(DateTime nowUtc)
        {
            if (_cleanupApplication is null ||
                _cleanupApplication is not IManagedResourceLifecycleWork lifecycle ||
                !lifecycle.LifecycleWorkPending)
            {
                return;
            }

            lifecycle.AdvanceLifecycle(nowUtc);
        }

        /// <summary>Cancels this executable's pending cleanup close without touching the process again.</summary>
        public void CancelCleanup()
        {
            _cleanupApplication?.CancelPendingRecovery();
        }

        /// <summary>Releases the cleanup lifecycle without closing or otherwise changing the helper.</summary>
        public void Dispose()
        {
            if (_cleanupApplication is null)
                return;

            _cleanupApplication.ErrorOccurred -= CleanupApplicationFailed;

            if (_cleanupApplication is IRecoverableResourceErrorSource recoverableSource)
                recoverableSource.ErrorCleared -= CleanupApplicationRecovered;
            _cleanupApplication.Dispose();
            _cleanupApplication = null;
        }

        /// <summary>Checks whether any enabled profile reference currently needs the executable.</summary>
        /// <returns><see langword="true"/> when cleanup must not proceed.</returns>
        private bool IsRequiredByAnyOwner()
        {
            return _references.Any(reference => reference.RequiresHelperToRemainRunning());
        }

        /// <summary>Converts the managed-application failure into a group-level cleanup failure.</summary>
        /// <param name="resource">The cleanup lifecycle that reported the failure.</param>
        /// <param name="message">The close failure text.</param>
        private void CleanupApplicationFailed(IManagedResource resource, string message)
        {
            _reportFailure(
                Path.GetFileName(_path),
                message,
                _cleanupTargets
            );
        }

        /// <summary>Clears registry-level cleanup error state after a later successful sweep.</summary>
        /// <param name="resource">The cleanup application that recovered.</param>
        private void CleanupApplicationRecovered(IManagedResource resource)
        {
            _reportRecovery();
        }
    }

    /// <summary>Couples one profile-specific application configuration with its runtime activity predicate.</summary>
    private sealed class ApplicationReference
    {
        /// <summary>Creates one immutable profile reference.</summary>
        /// <param name="configuration">The profile-specific application settings.</param>
        /// <param name="owner">The unique profile owner.</param>
        /// <param name="keepsResourcesActive">Reports whether this owner still needs its resources.</param>
        public ApplicationReference(
            ManagedApplicationConfig configuration,
            object owner,
            Func<bool> keepsResourcesActive)
        {
            Configuration = configuration;
            Owner = owner;
            KeepsResourcesActive = keepsResourcesActive;
        }

        /// <summary>Gets the profile-specific application settings.</summary>
        public ManagedApplicationConfig Configuration { get; }

        /// <summary>Gets the unique profile owner.</summary>
        public object Owner { get; }

        /// <summary>Gets the predicate that reports whether the owner still needs its resources.</summary>
        public Func<bool> KeepsResourcesActive { get; }

        /// <summary>Reports whether active use or persistent-helper configuration protects this executable.</summary>
        public bool RequiresHelperToRemainRunning()
        {
            return Configuration.LeaveRunningAfterProfileStops || KeepsResourcesActive();
        }
    }
}
