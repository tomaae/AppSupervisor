using AppSupervisor.Core;

namespace AppSupervisor.Triggers;

/// <summary>Suppresses trigger polling until another profile is active and fully started.</summary>
internal sealed class ProfileDependencyTrigger : ITrigger
{
    private readonly ITrigger _inner;
    private readonly Func<bool> _isDependencyRunning;

    internal ProfileDependencyTrigger(ITrigger inner, Func<bool> isDependencyRunning)
    {
        _inner = inner;
        _isDependencyRunning = isDependencyRunning;
    }

    /// <summary>Checks the monitored trigger only while its profile prerequisite is ready.</summary>
    public bool IsActive() => _isDependencyRunning() && _inner.IsActive();
}
