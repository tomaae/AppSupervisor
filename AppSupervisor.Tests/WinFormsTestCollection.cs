namespace AppSupervisor.Tests;

/// <summary>Serializes tests that create Windows Forms controls or message pumps.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WinFormsTestCollection
{
    public const string Name = "Windows Forms";
}
