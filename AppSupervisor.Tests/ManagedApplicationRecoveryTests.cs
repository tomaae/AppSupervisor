using AppSupervisor.Core;
using AppSupervisor.Resources;

namespace AppSupervisor.Tests;

/// <summary>Verifies bounded automatic process launch recovery.</summary>
public sealed class ManagedApplicationRecoveryTests
{
    [Fact]
    public void FailedLaunches_StopAfterFiveAttemptsWithFiveSecondSpacing()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        int launches = 0;
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor-missing-{Guid.NewGuid():N}.exe"
        );
        using var application = new ManagedApplication(
            new ManagedApplicationConfig { Path = path, Restart = true },
            TimeSpan.Zero,
            shouldRemainRunning: null,
            processIdProvider: () => new HashSet<int>(),
            timeProvider: time,
            processStarter: _ =>
            {
                launches++;
                throw new InvalidOperationException("Launch rejected.");
            }
        );
        var errors = new List<string>();
        application.ErrorOccurred += (_, message) => errors.Add(message);

        application.Activate();
        ((IManagedResourceLifecycleWork)application).AdvanceLifecycle(
            time.GetUtcNow().UtcDateTime
        );
        Assert.Equal(1, launches);
        application.Supervise();

        for (int attempt = 2; attempt <= AutomaticRecoveryBudget.MaximumAttempts; attempt++)
        {
            time.Advance(AutomaticRecoveryBudget.RetryDelay);
            application.Supervise();
            ((IManagedResourceLifecycleWork)application).AdvanceLifecycle(
                time.GetUtcNow().UtcDateTime
            );
            Assert.Equal(attempt, launches);
        }

        time.Advance(TimeSpan.FromMinutes(1));
        application.Supervise();
        ((IManagedResourceLifecycleWork)application).AdvanceLifecycle(
            time.GetUtcNow().UtcDateTime
        );

        Assert.Equal(5, launches);
        Assert.Contains("attempt 5 of 5", errors.Last());
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
