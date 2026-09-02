using AppSupervisor.Notifications;
using AppSupervisor.StreamDeck;

namespace AppSupervisor;

/// <summary>Handles commands received from the companion Stream Deck plugin.</summary>
public partial class TrayApplicationContext
{
    private void StreamDeckProfileLaunchRequested(string profileId)
    {
        QueueSupervisionWork(
            () => LaunchMonitoredProfile(profileId),
            static () => { }
        );
    }

    private void LaunchMonitoredProfile(string profileId)
    {
        SupervisorProfileConfig? profile = _configuration.Profiles.FirstOrDefault(candidate =>
            string.Equals(candidate.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            ReportStreamDeckLaunchFailure(
                "The selected profile no longer exists. Select a profile again in Stream Deck."
            );
            return;
        }

        try
        {
            MonitoredProfileLaunchOutcome outcome = MonitoredProfileLauncher.Launch(profile);
            SupervisorLog.WriteInformation(outcome == MonitoredProfileLaunchOutcome.Started
                ? $"Stream Deck launched monitored profile '{profile.Name}'."
                : $"Stream Deck skipped launch for already-running profile '{profile.Name}'.");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or
                System.ComponentModel.Win32Exception)
        {
            ReportStreamDeckLaunchFailure(exception.Message, exception);
        }
    }

    private void ReportStreamDeckLaunchFailure(string message, Exception? exception = null)
    {
        if (exception is null)
            SupervisorLog.WriteWarning($"Stream Deck monitored-app launch failed. {message}");
        else
            SupervisorLog.WriteError("Stream Deck monitored-app launch failed.", exception);

        PublishSystemNotification(
            NotificationSeverity.Error,
            "Stream Deck launch failed",
            message
        );
    }
}
