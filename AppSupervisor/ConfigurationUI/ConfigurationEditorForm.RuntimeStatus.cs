using AppSupervisor.Configuration;
using AppSupervisor.Core;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Refreshes cached application and service state while the configuration window is visible.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly Func<ConfigurationRuntimeStatusSnapshot>? _runtimeStatusReader;
    private readonly System.Windows.Forms.Timer _runtimeStatusTimer = new() { Interval = 250 };
    private ConfigurationRuntimeStatusSnapshot _runtimeStatusSnapshot =
        ConfigurationRuntimeStatusSnapshot.Empty;
    private bool _runtimeStatusDisposed;

    /// <summary>Connects the query-free snapshot reader without starting work before the form is shown.</summary>
    private void InitializeRuntimeStatus()
    {
        if (_runtimeStatusReader is null)
            return;

        Shown += RuntimeStatusFormShown;
        FormClosed += RuntimeStatusFormClosed;
        _runtimeStatusTimer.Tick += RuntimeStatusTimerTick;
    }

    private void RuntimeStatusFormShown(object? sender, EventArgs e)
    {
        RefreshRuntimeStatusSnapshot();
        _runtimeStatusTimer.Start();
    }

    private void RuntimeStatusTimerTick(object? sender, EventArgs e) =>
        RefreshRuntimeStatusSnapshot();

    private void RuntimeStatusFormClosed(object? sender, FormClosedEventArgs e) =>
        _runtimeStatusTimer.Stop();

    /// <summary>Reads one immutable in-memory snapshot and repaints without rebinding list items.</summary>
    internal void RefreshRuntimeStatusSnapshot()
    {
        if (_runtimeStatusReader is null || _runtimeStatusDisposed)
            return;

        ConfigurationRuntimeStatusSnapshot snapshot;

        try
        {
            snapshot = _runtimeStatusReader();
        }
        catch
        {
            return;
        }

        if (ReferenceEquals(snapshot, _runtimeStatusSnapshot))
            return;

        _runtimeStatusSnapshot = snapshot;
        _resourceList.Invalidate();
    }

    /// <summary>Returns cached status for an application or service in the selected profile.</summary>
    private ConfigurationResourceRuntimeStatus GetRuntimeStatus(
        ManagedResourceConfig resource)
    {
        if (SelectedProfile is not SupervisorProfileConfig profile)
            return ConfigurationResourceRuntimeStatus.Unknown;

        return _runtimeStatusSnapshot.GetStatus(profile.ProfileId, resource.ResourceId);
    }

    /// <summary>Limits second status icons to process and Windows-service resources.</summary>
    internal static bool UsesRuntimeStatusIcon(ManagedResourceConfig resource) =>
        resource is ManagedApplicationConfig or ManagedServiceConfig;

    /// <summary>Stops the form-owned refresh timer and detaches its visibility event.</summary>
    private void DisposeRuntimeStatus()
    {
        if (_runtimeStatusDisposed)
            return;

        _runtimeStatusDisposed = true;
        Shown -= RuntimeStatusFormShown;
        FormClosed -= RuntimeStatusFormClosed;
        _runtimeStatusTimer.Stop();
        _runtimeStatusTimer.Tick -= RuntimeStatusTimerTick;
        _runtimeStatusTimer.Dispose();
    }

    internal bool RuntimeStatusRefreshActive =>
        !_runtimeStatusDisposed && _runtimeStatusTimer.Enabled;
}
