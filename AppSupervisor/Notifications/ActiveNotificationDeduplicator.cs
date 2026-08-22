namespace AppSupervisor.Notifications;

/// <summary>Tracks recoverable notification identities while their underlying errors remain active.</summary>
internal sealed class ActiveNotificationDeduplicator<TIdentity>
    where TIdentity : notnull
{
    private readonly HashSet<TIdentity> _active = [];

    /// <summary>Marks an error active and returns whether its notification has not been published yet.</summary>
    public bool TryActivate(TIdentity identity) => _active.Add(identity);

    /// <summary>Clears every identity that belongs to a recovered resource.</summary>
    public void ClearWhere(Predicate<TIdentity> match) => _active.RemoveWhere(match);

    /// <summary>Clears all identities after runtime configuration is replaced.</summary>
    public void Clear() => _active.Clear();
}
