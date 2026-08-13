namespace AppSupervisor;

/// <summary>Configures one explicit nonblocking delay in a profile's startup sequence.</summary>
public sealed class DelayResourceConfig : ManagedResourceConfig
{
    /// <summary>Gets or sets the time that must elapse before the following resource starts.</summary>
    public int DurationMilliseconds { get; set; } = 1_000;
}
