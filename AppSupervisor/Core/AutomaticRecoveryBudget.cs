namespace AppSupervisor.Core;

/// <summary>
/// Limits one continuous automatic restart or recovery sequence and enforces a quiet period
/// between failed attempts.
/// </summary>
internal sealed class AutomaticRecoveryBudget
{
    internal const int MaximumAttempts = 5;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private DateTime _nextAttemptUtc = DateTime.MinValue;

    /// <summary>Gets the number of attempts made since the last confirmed success or reset.</summary>
    internal int Attempts { get; private set; }

    /// <summary>Gets whether the current recovery sequence has used every allowed attempt.</summary>
    internal bool Exhausted => Attempts >= MaximumAttempts;

    /// <summary>Gets the earliest time at which another failed-sequence attempt may begin.</summary>
    internal DateTime NextAttemptUtc => _nextAttemptUtc;

    /// <summary>Starts one allowed and due attempt.</summary>
    /// <param name="nowUtc">The current supervisor timestamp.</param>
    /// <returns><see langword="true"/> when the caller owns a new attempt.</returns>
    internal bool TryBeginAttempt(DateTime nowUtc)
    {
        if (Exhausted || nowUtc < _nextAttemptUtc)
            return false;

        Attempts++;
        return true;
    }

    /// <summary>Schedules the next attempt after the universal recovery delay.</summary>
    /// <param name="nowUtc">The timestamp at which the current attempt failed.</param>
    internal void RecordFailure(DateTime nowUtc)
    {
        if (!Exhausted)
            _nextAttemptUtc = nowUtc + RetryDelay;
    }

    /// <summary>Clears consecutive attempt state after confirmed success or lifecycle cancellation.</summary>
    internal void Reset()
    {
        Attempts = 0;
        _nextAttemptUtc = DateTime.MinValue;
    }

    /// <summary>Adds consistent attempt, delay, and exhaustion detail to a user-visible failure.</summary>
    /// <param name="message">The operation-specific failure message.</param>
    /// <returns>The message followed by the current automatic-recovery state.</returns>
    internal string DescribeFailure(string message)
    {
        if (Exhausted)
        {
            return $"{message} Automatic recovery stopped after attempt " +
                $"{Attempts} of {MaximumAttempts}; no more attempts will be made until " +
                "the resource is successfully confirmed or a new lifecycle begins.";
        }

        return $"{message} Automatic recovery attempt {Attempts} of {MaximumAttempts} failed; " +
            $"the next attempt is allowed in {(int)RetryDelay.TotalSeconds} seconds.";
    }
}
