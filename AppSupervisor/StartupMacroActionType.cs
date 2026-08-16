namespace AppSupervisor;

/// <summary>Selects one ordered action in a helper application's startup macro.</summary>
public enum StartupMacroActionType
{
    Delay,
    Hotkey,
    MoveWindow,
    ResizeWindow,
    Minimize,
    Maximize,
    Restore,
    BringToFront
}
