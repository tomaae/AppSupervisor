namespace AppSupervisor.Core;

/// <summary>Lets a managed resource clear a previously reported lifecycle error after normal operation resumes.</summary>
internal interface IRecoverableResourceErrorSource
{
    /// <summary>Occurs once when the resource has recovered from its active lifecycle error.</summary>
    event Action<IManagedResource>? ErrorCleared;
}
