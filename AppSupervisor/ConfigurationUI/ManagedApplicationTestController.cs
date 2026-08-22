using AppSupervisor.Configuration;
using AppSupervisor.Core;
using AppSupervisor.Resources;

namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Pumps a detached managed application through the same serialized lifecycle used by active profiles.
/// </summary>
internal sealed class ManagedApplicationTestController : IHelperTestController
{
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<Action, Task> _executeSerialized;
    private readonly Func<string, bool> _isProfileBusy;
    private readonly Func<string, bool> _isHelperRequired;
    private readonly Func<ManagedApplicationConfig, Func<bool>, IManagedApplicationLifecycle>
        _applicationFactory;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _disposeCancellation = new();

    private IManagedApplicationLifecycle? _application;
    private string? _runtimePath;
    private Task? _pumpTask;
    private TaskCompletionSource? _startCompletion;
    private TaskCompletionSource? _stopCompletion;
    private string? _lastLifecycleError;
    private HelperTestState _state;
    private volatile bool _pumpFailed;
    private bool _disposed;

    /// <summary>Creates a controller backed by production managed-application behavior.</summary>
    internal ManagedApplicationTestController(
        Func<Action, Task> executeSerialized,
        Func<string, bool> isProfileBusy,
        Func<string, bool> isHelperRequired)
        : this(
            executeSerialized,
            isProfileBusy,
            isHelperRequired,
            (configuration, shouldRemainRunning) => new ManagedApplication(
                configuration,
                TimeSpan.FromSeconds(20),
                shouldRemainRunning
            )
        )
    {
    }

    /// <summary>Creates a controller with an injectable lifecycle for focused tests.</summary>
    internal ManagedApplicationTestController(
        Func<Action, Task> executeSerialized,
        Func<string, bool> isProfileBusy,
        Func<string, bool> isHelperRequired,
        Func<ManagedApplicationConfig, Func<bool>, IManagedApplicationLifecycle>
            applicationFactory)
    {
        _executeSerialized = executeSerialized;
        _isProfileBusy = isProfileBusy;
        _isHelperRequired = isHelperRequired;
        _applicationFactory = applicationFactory;
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public HelperTestState State
    {
        get
        {
            lock (_stateLock)
                return _state;
        }
    }

    /// <inheritdoc />
    public async Task<bool> CanStartAsync(string profileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (State != HelperTestState.Idle)
            return false;

        bool canStart = false;
        await _executeSerialized(() => canStart = !_isProfileBusy(profileId));
        return canStart && State == HelperTestState.Idle;
    }

    /// <inheritdoc />
    public async Task StartAsync(
        string profileId,
        ManagedApplicationConfig configuration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commandGate.WaitAsync(cancellationToken);
        Task startTask;

        try
        {
            if (State != HelperTestState.Idle)
                throw new InvalidOperationException("A helper test is already in progress.");

            SetState(HelperTestState.Starting);
            _startCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            try
            {
                await _executeSerialized(() => InitializeTest(profileId, configuration));
            }
            catch
            {
                await ResetAfterInitializationFailureAsync();
                throw;
            }

            EnsurePumpRunning();
            startTask = _startCompletion.Task;
        }
        finally
        {
            _commandGate.Release();
        }

        await startTask.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commandGate.WaitAsync(cancellationToken);
        Task? stopTask = null;

        try
        {
            HelperTestState state = State;

            if (state == HelperTestState.Idle)
                return;

            if (state == HelperTestState.Stopping)
            {
                stopTask = _stopCompletion?.Task;
            }
            else
            {
                _stopCompletion = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _lastLifecycleError = null;
                SetState(HelperTestState.Stopping);
                _startCompletion?.TrySetException(new OperationCanceledException(
                    "The helper test was stopped before startup completed."
                ));
                await _executeSerialized(BeginStop);
                EnsurePumpRunning();
                stopTask = _stopCompletion.Task;
            }
        }
        finally
        {
            _commandGate.Release();
        }

        if (stopTask is not null)
            await stopTask.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            if (State != HelperTestState.Idle)
                await StopAsync();
        }
        catch (Exception exception)
        {
            SupervisorLog.WriteError(
                "The helper test could not be closed while its controller was disposed.",
                exception
            );
        }

        _disposed = true;
        _disposeCancellation.Cancel();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_application is not null)
        {
            try
            {
                await _executeSerialized(DisposeApplication);
            }
            catch (OperationCanceledException)
            {
                DisposeApplication();
            }
        }

        _disposeCancellation.Dispose();
        _commandGate.Dispose();
        StateChanged = null;
    }

    /// <summary>Builds and activates the detached production lifecycle under the supervision gate.</summary>
    private void InitializeTest(
        string profileId,
        ManagedApplicationConfig configuration)
    {
        if (_isProfileBusy(profileId))
        {
            throw new InvalidOperationException(
                "This profile is active or is still completing its normal shutdown."
            );
        }

        ManagedApplicationConfig testConfiguration = ConfigJson.Clone(configuration);
        testConfiguration.LeaveRunningAfterProfileStops = false;
        _runtimePath = JavaLauncherDetector.ResolveRuntimePath(testConfiguration.Path);
        string helperPath = testConfiguration.Path;
        _application = _applicationFactory(
            testConfiguration,
            () => _isHelperRequired(helperPath)
        );
        _application.ErrorOccurred += ApplicationErrorOccurred;

        ProcessPathSnapshot.BeginCycle(preferSharedSnapshot: false);
        _application.Initialize();
        _application.Activate();
    }

    /// <summary>Cancels recovery work exactly as trigger loss does, then skips directly to deactivation.</summary>
    private void BeginStop()
    {
        if (_application is null)
        {
            CompleteStop();
            return;
        }

        ProcessPathSnapshot.BeginCycle(preferSharedSnapshot: false);
        _application.CancelPendingRecovery();
        _application.Deactivate();
    }

    /// <summary>Advances startup macros, window work, and close attempts until the requested phase completes.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   State != HelperTestState.Idle)
            {
                await _executeSerialized(AdvanceLifecycle);

                if (State == HelperTestState.Idle)
                    break;

                await Task.Delay(ActivePollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _pumpFailed = true;
            string message = $"The helper test lifecycle could not advance: {exception.Message}";

            if (State == HelperTestState.Starting)
            {
                SetState(HelperTestState.Running);
                _startCompletion?.TrySetException(new InvalidOperationException(
                    message,
                    exception
                ));
            }
            else if (State == HelperTestState.Stopping)
            {
                SetState(HelperTestState.Running);
                _stopCompletion?.TrySetException(new InvalidOperationException(
                    message,
                    exception
                ));
            }
        }
    }

    /// <summary>Starts or restarts the lifecycle pump after an earlier unexpected pump failure.</summary>
    private void EnsurePumpRunning()
    {
        if (_pumpFailed || _pumpTask is null || _pumpTask.IsCompleted)
        {
            _pumpFailed = false;
            _pumpTask = PumpAsync(_disposeCancellation.Token);
        }
    }

    /// <summary>Runs one fresh process-observation and lifecycle pass.</summary>
    private void AdvanceLifecycle()
    {
        IManagedApplicationLifecycle? application = _application;
        string? runtimePath = _runtimePath;

        if (application is null || runtimePath is null)
            return;

        ProcessPathSnapshot.BeginCycle(preferSharedSnapshot: false);

        IManagedResourceLifecycleWork? lifecycle =
            application as IManagedResourceLifecycleWork;

        if (lifecycle?.LifecycleWorkPending == true)
        {
            lifecycle.AdvanceLifecycle(SupervisorTime.UtcNow);
        }

        bool transitionPending = ProcessPathSnapshot.HasTransition(runtimePath);
        bool lifecycleWorkPending = lifecycle?.LifecycleWorkPending == true;

        if (State == HelperTestState.Starting)
        {
            if (application.IsStarted())
            {
                SetState(HelperTestState.Running);
                _startCompletion?.TrySetResult();
            }
            else if (!transitionPending && !lifecycleWorkPending)
            {
                string error = _lastLifecycleError ??
                    $"Could not confirm that {application.DisplayName} started.";
                FailStart(error);
            }

            return;
        }

        if (State != HelperTestState.Stopping || transitionPending || lifecycleWorkPending)
            return;

        if (!application.IsRunning() || _isHelperRequired(application.Config.Path))
        {
            CompleteStop();
            return;
        }

        string closeError = _lastLifecycleError ??
            $"Could not confirm that {application.DisplayName} closed.";
        SetState(HelperTestState.Running);
        _stopCompletion?.TrySetException(new InvalidOperationException(closeError));
    }

    /// <summary>Records the latest production lifecycle error for the pending editor operation.</summary>
    private void ApplicationErrorOccurred(IManagedResource resource, string message)
    {
        _lastLifecycleError = message;
    }

    /// <summary>Returns to idle after a failed start and surfaces its production error.</summary>
    private void FailStart(string message)
    {
        DisposeApplication();
        SetState(HelperTestState.Idle);
        _startCompletion?.TrySetException(new InvalidOperationException(message));
    }

    /// <summary>Releases a successfully stopped or production-owned helper test.</summary>
    private void CompleteStop()
    {
        DisposeApplication();
        SetState(HelperTestState.Idle);
        _stopCompletion?.TrySetResult();
    }

    /// <summary>Resets state when validation, activity checking, or lifecycle construction fails.</summary>
    private async Task ResetAfterInitializationFailureAsync()
    {
        await _executeSerialized(DisposeApplication);
        SetState(HelperTestState.Idle);
    }

    /// <summary>Unsubscribes and disposes the detached lifecycle without changing external processes.</summary>
    private void DisposeApplication()
    {
        if (_application is not null)
        {
            _application.ErrorOccurred -= ApplicationErrorOccurred;
            _application.Dispose();
        }

        _application = null;
        _runtimePath = null;
        _lastLifecycleError = null;
    }

    /// <summary>Changes state once and notifies the editor outside the state lock.</summary>
    private void SetState(HelperTestState state)
    {
        bool changed;

        lock (_stateLock)
        {
            changed = _state != state;
            _state = state;
        }

        if (changed)
            StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
