namespace AppSupervisor;

/// <summary>
/// Selects the transport used by a listener health check.
/// </summary>
public enum ListenerProtocol
{
    /// <summary>Checks a TCP endpoint in the LISTENING state.</summary>
    Tcp,

    /// <summary>Checks a bound UDP endpoint.</summary>
    Udp
}
