namespace AppSupervisor.Resources;

/// <summary>Advances a helper's ordered startup macro without blocking the lifecycle timer.</summary>
internal sealed class StartupMacroExecutor
{
    private const int RequiredGeometryStableObservations = 3;
    private static readonly TimeSpan RequiredGeometryStableDuration = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<StartupMacroActionConfig> _actions;
    private readonly Func<IReadOnlySet<int>> _processIdProvider;
    private readonly Action<string> _failureReporter;
    private readonly Action<bool> _completed;
    private readonly Func<StartupMacroActionConfig, IReadOnlySet<int>,
        StartupMacroWindowActions.ExecutionResult> _execute;

    private int _actionIndex;
    private DateTime? _delayUntilUtc;
    private WindowMinimizeOperation? _minimizeOperation;
    private DateTime? _geometryStableSinceUtc;
    private int _geometryStableObservations;
    private bool _failed;

    public StartupMacroExecutor(
        IReadOnlyList<StartupMacroActionConfig> actions,
        Func<IReadOnlySet<int>> processIdProvider,
        Action<string> failureReporter,
        Action<bool> completed,
        Func<StartupMacroActionConfig, IReadOnlySet<int>,
            StartupMacroWindowActions.ExecutionResult>? execute = null)
    {
        _actions = actions;
        _processIdProvider = processIdProvider;
        _failureReporter = failureReporter;
        _completed = completed;
        _execute = execute ?? StartupMacroWindowActions.Execute;
    }

    /// <summary>Gets whether a started macro still has actions to advance.</summary>
    public bool Pending { get; private set; }

    /// <summary>Starts the sequence at its first action.</summary>
    public void Start()
    {
        Cancel();

        if (_actions.Count == 0)
            return;

        _actionIndex = 0;
        _failed = false;
        Pending = true;
    }

    /// <summary>Stops the current sequence without reporting an error.</summary>
    public void Cancel()
    {
        Pending = false;
        _actionIndex = 0;
        _delayUntilUtc = null;
        _minimizeOperation = null;
        ResetGeometryStability();
        _failed = false;
    }

    /// <summary>Runs ready actions until the sequence reaches a delay or window retry.</summary>
    public void Advance(DateTime nowUtc)
    {
        while (Pending && _actionIndex < _actions.Count)
        {
            StartupMacroActionConfig action = _actions[_actionIndex];

            if (action.Type == StartupMacroActionType.Delay)
            {
                _delayUntilUtc ??= nowUtc + TimeSpan.FromMilliseconds(action.DelayMilliseconds ?? 0);

                if (nowUtc < _delayUntilUtc)
                    return;

                CompleteAction();
                continue;
            }

            StartupMacroWindowActions.ExecutionResult result;
            if (action.Type == StartupMacroActionType.Minimize)
            {
                _minimizeOperation ??= new WindowMinimizeOperation(nowUtc);
                try
                {
                    bool? minimized = _minimizeOperation.Advance(
                        nowUtc,
                        () => _execute(action, _processIdProvider()).AppliedSuccessfully
                    );
                    if (minimized is null)
                        return;

                    result = minimized.Value
                        ? StartupMacroWindowActions.ExecutionResult.Success("Helper windows remain minimized.")
                        : StartupMacroWindowActions.ExecutionResult.Failure(
                            $"Could not keep helper windows minimized within {WindowMinimizeOperation.TimeoutMilliseconds / 1000} seconds."
                        );
                }
                catch (Exception exception)
                {
                    result = StartupMacroWindowActions.ExecutionResult.Failure(exception.Message);
                }
            }
            else
            {
                result = _execute(action, _processIdProvider());
            }
            bool moveOrResize = IsMoveOrResize(action);

            if (result.Status == StartupMacroWindowActions.ExecutionStatus.WindowUnavailable)
            {
                ResetGeometryStability();
                return;
            }

            if (moveOrResize &&
                result.Status == StartupMacroWindowActions.ExecutionStatus.WindowAdjusted)
            {
                ResetGeometryStability();
                return;
            }

            if (moveOrResize &&
                result.Status == StartupMacroWindowActions.ExecutionStatus.Succeeded)
            {
                _geometryStableSinceUtc ??= nowUtc;
                _geometryStableObservations++;

                if (_geometryStableObservations < RequiredGeometryStableObservations ||
                    nowUtc - _geometryStableSinceUtc < RequiredGeometryStableDuration)
                {
                    return;
                }
            }

            if (result.Status != StartupMacroWindowActions.ExecutionStatus.Succeeded)
            {
                _failed = true;
                _failureReporter(
                    $"Startup macro action {_actionIndex + 1} ({action.Type}) failed: {result.Detail}"
                );
            }

            CompleteAction();
        }

        if (!Pending)
            return;

        Pending = false;
        _completed(!_failed);
    }

    private void CompleteAction()
    {
        _actionIndex++;
        _delayUntilUtc = null;
        _minimizeOperation = null;
        ResetGeometryStability();
    }

    private static bool IsMoveOrResize(StartupMacroActionConfig action) =>
        action.Type is StartupMacroActionType.MoveWindow or StartupMacroActionType.ResizeWindow;

    private void ResetGeometryStability()
    {
        _geometryStableSinceUtc = null;
        _geometryStableObservations = 0;
    }
}
