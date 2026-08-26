namespace AppSupervisor;

/// <summary>Defines what an MQTT resource publishes when its profile deactivates.</summary>
public enum MqttDeactivationBehavior
{
    /// <summary>Leaves the activation publish in place and performs no inverse action.</summary>
    OneShot,

    /// <summary>Publishes a user-configured deterministic reverse message.</summary>
    PublishConfiguredPayload,

    /// <summary>Restores the exact retained state captured before activation.</summary>
    RestoreRetainedState
}
