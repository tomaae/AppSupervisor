using AppSupervisor.Core;

namespace AppSupervisor.Tests;

/// <summary>Verifies grouping of same-executable process trees into logical application instances.</summary>
public sealed class ProcessPathSnapshotTests
{
    /// <summary>Confirms one Electron root and its subprocesses represent one application instance.</summary>
    [Fact]
    public void FindIndependentRootProcessIds_ElectronProcessTree_ReturnsOneRoot()
    {
        int[] processIds = [100, 101, 102, 103];
        var parents = new Dictionary<int, int>
        {
            [100] = 50,
            [101] = 100,
            [102] = 100,
            [103] = 101
        };

        IReadOnlySet<int> roots = ProcessPathSnapshot.FindIndependentRootProcessIds(
            processIds,
            parents
        );

        Assert.Equal([100], roots);
    }

    /// <summary>Confirms two independent same-path roots remain duplicate application instances.</summary>
    [Fact]
    public void FindIndependentRootProcessIds_TwoProcessTrees_ReturnsTwoRoots()
    {
        int[] processIds = [100, 101, 200, 201];
        var parents = new Dictionary<int, int>
        {
            [100] = 50,
            [101] = 100,
            [200] = 60,
            [201] = 200
        };

        IReadOnlySet<int> roots = ProcessPathSnapshot.FindIndependentRootProcessIds(
            processIds,
            parents
        );

        Assert.Equal([100, 200], roots.Order());
    }

    /// <summary>Confirms unavailable parent information cannot trigger destructive duplicate normalization.</summary>
    [Fact]
    public void FindIndependentRootProcessIds_MissingParentRecords_AssumesOneInstance()
    {
        int[] processIds = [100, 101];

        IReadOnlySet<int> roots = ProcessPathSnapshot.FindIndependentRootProcessIds(
            processIds,
            new Dictionary<int, int>()
        );

        Assert.Single(roots);
    }

    /// <summary>Confirms malformed cyclic ancestry cannot hang grouping or create false duplicate roots.</summary>
    [Fact]
    public void FindIndependentRootProcessIds_CyclicParents_CompletesSafely()
    {
        int[] processIds = [100, 101];
        var parents = new Dictionary<int, int>
        {
            [100] = 101,
            [101] = 100
        };

        IReadOnlySet<int> roots = ProcessPathSnapshot.FindIndependentRootProcessIds(
            processIds,
            parents
        );

        Assert.Single(roots);
    }
}
