namespace AppSupervisor.ConfigurationUI;

/// <summary>Identifies the editor-visible phase of one production-lifecycle helper test.</summary>
internal enum HelperTestState
{
    Idle,
    Starting,
    Running,
    Stopping
}

/// <summary>Coordinates one helper test with the running supervisor.</summary>
internal interface IHelperTestController : IAsyncDisposable
{
    /// <summary>Occurs when the test phase changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Gets the current test phase.</summary>
    HelperTestState State { get; }

    /// <summary>Checks whether the selected profile is inactive and a test may start.</summary>
    Task<bool> CanStartAsync(string profileId);

    /// <summary>Starts a detached copy of the helper through its production lifecycle.</summary>
    Task StartAsync(
        string profileId,
        ManagedApplicationConfig configuration,
        CancellationToken cancellationToken = default
    );

    /// <summary>Immediately begins the production helper-close lifecycle and waits for completion.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
