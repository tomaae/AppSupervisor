namespace AppSupervisor.Core;

/// <summary>Exposes cancellable background work that must settle before manual pause is confirmed.</summary>
internal interface IPauseDrainWork
{
    /// <summary>Gets whether a previously accepted background operation is still finishing.</summary>
    bool PauseDrainPending { get; }

    /// <summary>Stops producing new work and requests cancellation of work that can be cancelled safely.</summary>
    void BeginPauseDrain();

    /// <summary>Reaps completed cancellation work without starting another operation.</summary>
    void AdvancePauseDrain();
}
