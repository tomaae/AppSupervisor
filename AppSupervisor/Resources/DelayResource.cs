using AppSupervisor.Core;
using AppSupervisor.Notifications;

namespace AppSupervisor.Resources;

/// <summary>Represents an explicit no-op startup entry whose configured duration delays later resources.</summary>
internal sealed class DelayResource(DelayResourceConfig configuration) : IManagedResource
{
    public event Action<IManagedResource, string>? ErrorOccurred
    {
        add { }
        remove { }
    }

    public string DisplayName => $"Delay {configuration.DurationMilliseconds:N0} ms";

    public IReadOnlyList<NotificationTarget> NotificationTargets => [];

    public void Activate() { }

    public ManagedResourceUpdate Supervise() => ManagedResourceUpdate.None;

    public void CancelPendingRecovery() { }

    public void SuspendMonitoring() { }

    public void Deactivate() { }

    public void SuperviseDeactivation() { }

    public void Dispose() { }
}
