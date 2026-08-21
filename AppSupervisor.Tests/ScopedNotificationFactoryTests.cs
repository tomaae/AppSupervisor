using AppSupervisor.Core;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>Verifies that each notification scope retains only its explicit owner settings.</summary>
public sealed class ScopedNotificationFactoryTests
{
    [Fact]
    public void CreateResource_DifferentHelpers_UsesOnlyOwningHelperTargets()
    {
        using var popupHelper = new FakeResource(
            "Popup helper",
            [NotificationTarget.Popup]
        );
        using var windowsHelper = new FakeResource(
            "Windows helper",
            [NotificationTarget.Windows]
        );

        SupervisorNotification popupNotification = ScopedNotificationFactory.CreateResource(
            NotificationSeverity.Error,
            "Failed",
            "Popup helper failed.",
            popupHelper
        );
        SupervisorNotification windowsNotification = ScopedNotificationFactory.CreateResource(
            NotificationSeverity.Error,
            "Failed",
            "Windows helper failed.",
            windowsHelper
        );

        Assert.Equal([NotificationTarget.Popup], popupNotification.Targets);
        Assert.Equal([NotificationTarget.Windows], windowsNotification.Targets);
    }

    [Fact]
    public void CreateCheck_HelperTargetsDiffer_UsesOnlyCheckTargets()
    {
        var checkNotification = new ResourceNotification(
            "Listener",
            NotificationSeverity.Error,
            "Health check failed",
            "Listener failed.",
            [NotificationTarget.XsOverlay]
        );

        SupervisorNotification notification = ScopedNotificationFactory.CreateCheck(
            "Profile - Listener failed.",
            checkNotification
        );

        Assert.Equal([NotificationTarget.XsOverlay], notification.Targets);
    }

    [Fact]
    public void CreateSystem_HasIndependentPopupTarget()
    {
        SupervisorNotification notification = ScopedNotificationFactory.CreateSystem(
            NotificationSeverity.Error,
            "Configuration error",
            "Invalid configuration."
        );

        Assert.Equal([NotificationTarget.Popup], notification.Targets);
    }

    private sealed class FakeResource : IManagedResource
    {
        public FakeResource(
            string displayName,
            IReadOnlyList<NotificationTarget> notificationTargets)
        {
            DisplayName = displayName;
            NotificationTargets = notificationTargets;
        }

        public event Action<IManagedResource, string>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public string DisplayName { get; }

        public IReadOnlyList<NotificationTarget> NotificationTargets { get; }

        public void Activate()
        {
        }

        public ManagedResourceUpdate Supervise() => ManagedResourceUpdate.None;

        public void CancelPendingRecovery()
        {
        }

        public void SuspendMonitoring()
        {
        }

        public void Deactivate()
        {
        }

        public void SuperviseDeactivation()
        {
        }

        public void Dispose()
        {
        }
    }
}
