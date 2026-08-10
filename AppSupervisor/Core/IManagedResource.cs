using AppSupervisor.Notifications;

namespace AppSupervisor.Core;

/// <summary>
/// Defines the lifecycle operations required for a resource supervised by a supervisor profile.
/// </summary>
public interface IManagedResource : IDisposable
{
    /// <summary>
    /// Occurs when the resource cannot complete a requested supervision operation.
    /// </summary>
    event Action<IManagedResource, string>? ErrorOccurred;

    /// <summary>
    /// Gets the human-readable name used in supervision notifications.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Gets the presentation targets configured specifically for this helper resource.
    /// </summary>
    IReadOnlyList<NotificationTarget> NotificationTargets { get; }

    /// <summary>
    /// Performs one-time runtime initialization after a complete configuration is accepted.
    /// </summary>
    void Initialize()
    {
    }

    /// <summary>
    /// Ensures the resource is available when its supervisor profile becomes active.
    /// </summary>
    void Activate();

    /// <summary>
    /// Performs one active supervision cycle for the resource.
    /// </summary>
    /// <returns>A value describing whether the cycle restarted the resource.</returns>
    ManagedResourceUpdate Supervise();

    /// <summary>
    /// Cancels recovery and asynchronous work that must not continue while the supervisor profile is inactive.
    /// </summary>
    void CancelPendingRecovery();

    /// <summary>
    /// Suspends asynchronous monitoring work without starting, stopping, or closing the external resource.
    /// </summary>
    void SuspendMonitoring();

    /// <summary>
    /// Begins gracefully closing every matching resource instance after the profile's close timeout period expires.
    /// </summary>
    void Deactivate();

    /// <summary>
    /// Advances a previously requested graceful deactivation without starting the resource again.
    /// </summary>
    void SuperviseDeactivation();
}
