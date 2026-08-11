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

    /// <summary>Occurs when an inactive helper remains open after all configured close attempts.</summary>
    public event Action<string, string, IReadOnlyList<NotificationTarget>>? CleanupFailed;

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
            usage = new ApplicationUsage(key, ReportCleanupFailure);
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
    /// Advances already-started graceful close operations and cancels them if any profile needs the helper again.
    /// </summary>
    public void AdvanceCleanup()
    {
        if (_disposed || !_registrationCompleted)
            return;

        foreach (ApplicationUsage usage in _usages.Values)
            usage.AdvanceCleanup();
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

        _usages.Clear();
        CleanupFailed = null;
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

    /// <summary>Tracks every enabled profile reference and one optional cleanup lifecycle for an executable.</summary>
    private sealed class ApplicationUsage : IDisposable
    {
        private readonly string _path;
        private readonly Action<string, string, IReadOnlyList<NotificationTarget>> _reportFailure;
        private readonly List<ApplicationReference> _references = [];

        private IManagedApplicationLifecycle? _cleanupApplication;
        private IReadOnlyList<NotificationTarget> _cleanupTargets = [];

        /// <summary>Creates an empty executable usage group.</summary>
        /// <param name="path">The canonical executable path.</param>
        /// <param name="reportFailure">Receives completed cleanup failures.</param>
        public ApplicationUsage(
            string path,
            Action<string, string, IReadOnlyList<NotificationTarget>> reportFailure)
        {
            _path = path;
            _reportFailure = reportFailure;
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
                .Where(reference => reference.Configuration.EnsureClosedUntilNeeded)
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
        }

        /// <summary>Checks whether a different profile still requires this executable.</summary>
        /// <param name="requestingOwner">The owner whose deactivation is being evaluated.</param>
        /// <returns><see langword="true"/> when any other reference reports that resources remain needed.</returns>
        public bool IsRequiredByAnotherOwner(object requestingOwner)
        {
            return _references.Any(reference =>
                !ReferenceEquals(reference.Owner, requestingOwner) &&
                reference.KeepsResourcesActive());
        }

        /// <summary>Starts a graceful cleanup only when no profile currently needs the helper.</summary>
        public void BeginCleanupIfUnused()
        {
            if (_cleanupApplication is null)
                return;

            if (IsRequiredByAnyOwner())
            {
                _cleanupApplication.CancelPendingRecovery();
                return;
            }

            if (!_cleanupApplication.CloseOperationPending)
                _cleanupApplication.Deactivate();
        }

        /// <summary>Advances one pending cleanup close while continuously rechecking active profile usage.</summary>
        public void AdvanceCleanup()
        {
            if (_cleanupApplication is null ||
                !_cleanupApplication.CloseOperationPending)
            {
                return;
            }

            if (IsRequiredByAnyOwner())
            {
                _cleanupApplication.CancelPendingRecovery();
                return;
            }

            _cleanupApplication.SuperviseDeactivation();
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
            _cleanupApplication.Dispose();
            _cleanupApplication = null;
        }

        /// <summary>Checks whether any enabled profile reference currently needs the executable.</summary>
        /// <returns><see langword="true"/> when cleanup must not proceed.</returns>
        private bool IsRequiredByAnyOwner()
        {
            return _references.Any(reference => reference.KeepsResourcesActive());
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
    }
}
