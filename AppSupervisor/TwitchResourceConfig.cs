using AppSupervisor.Notifications;

namespace AppSupervisor;

/// <summary>Configures one Twitch broadcaster action governed by a supervisor profile.</summary>
public sealed class TwitchResourceConfig : ManagedResourceConfig
{
    public TwitchActionType Action { get; set; }
    public string Message { get; set; } = "";
    public int CommercialLengthSeconds { get; set; } = 30;
    public bool ModeEnabled { get; set; } = true;
    public int FollowerDurationMinutes { get; set; }
    public int SlowModeWaitSeconds { get; set; } = 30;
    public NotificationConfig Notifications { get; set; } = new();
}
