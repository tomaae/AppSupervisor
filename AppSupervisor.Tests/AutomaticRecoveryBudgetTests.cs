using AppSupervisor.Core;

namespace AppSupervisor.Tests;

/// <summary>Verifies the universal automatic restart and recovery attempt policy.</summary>
public sealed class AutomaticRecoveryBudgetTests
{
    [Fact]
    public void FailedAttempts_RequireFiveSecondsAndStopAfterFive()
    {
        var budget = new AutomaticRecoveryBudget();
        DateTime nowUtc = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

        for (int attempt = 1; attempt <= AutomaticRecoveryBudget.MaximumAttempts; attempt++)
        {
            Assert.True(budget.TryBeginAttempt(nowUtc));
            Assert.Equal(attempt, budget.Attempts);
            budget.RecordFailure(nowUtc);

            if (attempt == AutomaticRecoveryBudget.MaximumAttempts)
                break;

            Assert.False(budget.TryBeginAttempt(nowUtc));
            nowUtc += AutomaticRecoveryBudget.RetryDelay;
        }

        Assert.True(budget.Exhausted);
        Assert.False(budget.TryBeginAttempt(nowUtc + TimeSpan.FromHours(1)));
        Assert.Contains("attempt 5 of 5", budget.DescribeFailure("Failed."));
    }

    [Fact]
    public void Reset_ConfirmedSuccessStartsFreshSequence()
    {
        var budget = new AutomaticRecoveryBudget();
        DateTime nowUtc = DateTime.UtcNow;
        Assert.True(budget.TryBeginAttempt(nowUtc));
        budget.RecordFailure(nowUtc);

        budget.Reset();

        Assert.Equal(0, budget.Attempts);
        Assert.False(budget.Exhausted);
        Assert.True(budget.TryBeginAttempt(nowUtc));
    }
}
