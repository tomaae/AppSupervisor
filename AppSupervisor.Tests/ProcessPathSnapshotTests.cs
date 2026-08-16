using AppSupervisor.Core;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies grouping of same-executable process trees into logical application instances.</summary>
public sealed class ProcessPathSnapshotTests
{
    [Fact]
    public void ManagedApplication_ActivateDuringClose_QueuesStartAfterConfirmedClose()
    {
        string path = Environment.ProcessPath!;
        var closer = new object();
        using var application = new ManagedApplication(
            new ManagedApplicationConfig { Path = path },
            TimeSpan.Zero
        );

        try
        {
            ProcessPathSnapshot.RequestTransition(
                path,
                closer,
                ProcessLifecycleTransitionKind.Close
            );

            application.Activate();
            ProcessPathSnapshot.CompleteTransition(path, closer, succeeded: true);

            Assert.Equal(
                ProcessLifecycleTransitionKind.Start,
                ProcessPathSnapshot.GetOwnedTransition(path, application)
            );
        }
        finally
        {
            ProcessPathSnapshot.ReleaseOwner(closer);
        }
    }

    [Fact]
    public void RequestedProcessNames_ChooseSharedSnapshotOnlyAfterThreeDistinctLookups()
    {
        Assert.False(ProcessPathSnapshot.ShouldPreferSharedSnapshot(0));
        Assert.False(ProcessPathSnapshot.ShouldPreferSharedSnapshot(2));
        Assert.True(ProcessPathSnapshot.ShouldPreferSharedSnapshot(3));
    }
    /// <summary>Confirms only the first requester owns a start transition for a shared executable.</summary>
    [Fact]
    public void RequestTransition_SharedStart_HasSingleOwner()
    {
        string path = Path.Combine(Path.GetTempPath(), $"shared-{Guid.NewGuid():N}.exe");
        var first = new object();
        var second = new object();

        try
        {
            ProcessPathSnapshot.RequestTransition(
                path,
                first,
                ProcessLifecycleTransitionKind.Start
            );
            ProcessPathSnapshot.RequestTransition(
                path,
                second,
                ProcessLifecycleTransitionKind.Start
            );

            Assert.Equal(
                ProcessLifecycleTransitionKind.Start,
                ProcessPathSnapshot.GetOwnedTransition(path, first)
            );
            Assert.Null(ProcessPathSnapshot.GetOwnedTransition(path, second));

            ProcessPathSnapshot.CompleteTransition(path, first, succeeded: true);

            Assert.Equal(
                ProcessLifecycleTransitionKind.Start,
                ProcessPathSnapshot.GetOwnedTransition(path, second)
            );
        }
        finally
        {
            ProcessPathSnapshot.ReleaseOwner(first);
            ProcessPathSnapshot.ReleaseOwner(second);
        }
    }

    /// <summary>Confirms renewed demand waits behind close confirmation and is promoted afterward.</summary>
    [Fact]
    public void CompleteTransition_CloseThenStart_PromotesWaitingStarter()
    {
        string path = Path.Combine(Path.GetTempPath(), $"restart-{Guid.NewGuid():N}.exe");
        var closer = new object();
        var starter = new object();

        try
        {
            ProcessPathSnapshot.RequestTransition(
                path,
                closer,
                ProcessLifecycleTransitionKind.Close
            );
            ProcessPathSnapshot.RequestTransition(
                path,
                starter,
                ProcessLifecycleTransitionKind.Start
            );

            Assert.True(ProcessPathSnapshot.IsClosePending(path));
            Assert.Null(ProcessPathSnapshot.GetOwnedTransition(path, starter));

            ProcessPathSnapshot.CompleteTransition(path, closer, succeeded: true);

            Assert.Equal(
                ProcessLifecycleTransitionKind.Start,
                ProcessPathSnapshot.GetOwnedTransition(path, starter)
            );
        }
        finally
        {
            ProcessPathSnapshot.ReleaseOwner(closer);
            ProcessPathSnapshot.ReleaseOwner(starter);
        }
    }

    /// <summary>Confirms renewed demand behind a queued close is retained until close confirmation.</summary>
    [Fact]
    public void RequestTransition_StartCloseStart_PreservesFinalDemand()
    {
        string path = Path.Combine(Path.GetTempPath(), $"renewed-{Guid.NewGuid():N}.exe");
        var starter = new object();
        var closer = new object();

        try
        {
            ProcessPathSnapshot.RequestTransition(path, starter, ProcessLifecycleTransitionKind.Start);
            ProcessPathSnapshot.RequestTransition(path, closer, ProcessLifecycleTransitionKind.Close);
            ProcessPathSnapshot.RequestTransition(path, starter, ProcessLifecycleTransitionKind.Start);

            ProcessPathSnapshot.CompleteTransition(path, starter, succeeded: true);

            Assert.Equal(
                ProcessLifecycleTransitionKind.Close,
                ProcessPathSnapshot.GetOwnedTransition(path, closer)
            );

            ProcessPathSnapshot.CompleteTransition(path, closer, succeeded: true);

            Assert.Equal(
                ProcessLifecycleTransitionKind.Start,
                ProcessPathSnapshot.GetOwnedTransition(path, starter)
            );
        }
        finally
        {
            ProcessPathSnapshot.ReleaseOwner(starter);
            ProcessPathSnapshot.ReleaseOwner(closer);
        }
    }

    /// <summary>Confirms a failed close never promotes a duplicate replacement launch.</summary>
    [Fact]
    public void CompleteTransition_FailedClose_DropsWaitingStart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"failed-{Guid.NewGuid():N}.exe");
        var closer = new object();
        var starter = new object();

        try
        {
            ProcessPathSnapshot.RequestTransition(
                path,
                closer,
                ProcessLifecycleTransitionKind.Close
            );
            ProcessPathSnapshot.RequestTransition(
                path,
                starter,
                ProcessLifecycleTransitionKind.Start
            );

            ProcessPathSnapshot.CompleteTransition(path, closer, succeeded: false);

            Assert.False(ProcessPathSnapshot.HasTransition(path));
        }
        finally
        {
            ProcessPathSnapshot.ReleaseOwner(closer);
            ProcessPathSnapshot.ReleaseOwner(starter);
        }
    }

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
