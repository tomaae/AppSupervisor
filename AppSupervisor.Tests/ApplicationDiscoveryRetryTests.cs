using AppSupervisor.Discovery;

namespace AppSupervisor.Tests;

/// <summary>Verifies the shared Store and Steam installed-application retry policy.</summary>
public sealed class ApplicationDiscoveryRetryTests
{
    /// <summary>Confirms one transient provider failure is retried after the configured delay.</summary>
    [Fact]
    public void Execute_SecondAttemptSucceeds_ReturnsCatalog()
    {
        int attempts = 0;
        var delays = new List<TimeSpan>();

        string result = ApplicationDiscoveryRetry.Execute(
            "Test provider",
            () => ++attempts == 1
                ? throw new TimeoutException("Provider is busy.")
                : "catalog",
            attemptCount: 4,
            delays.Add
        );

        Assert.Equal("catalog", result);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromSeconds(3)], delays);
    }

    /// <summary>Confirms an exhausted provider retains its final exception and provider identity.</summary>
    [Fact]
    public void Execute_AllAttemptsFail_ThrowsDiscoveryException()
    {
        int attempts = 0;

        ApplicationDiscoveryException exception = Assert.Throws<ApplicationDiscoveryException>(
            () => ApplicationDiscoveryRetry.Execute<object>(
                "Steam",
                () =>
                {
                    attempts++;
                    throw new IOException("Library is temporarily unavailable.");
                },
                attemptCount: 4,
                _ => { }
            )
        );

        Assert.Equal(4, attempts);
        Assert.Equal("Steam", exception.ProviderName);
        Assert.Equal(4, exception.AttemptCount);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Contains("Library is temporarily unavailable", exception.Message);
    }

    /// <summary>Confirms cancellation is never converted into a discovery error or retried.</summary>
    [Fact]
    public void Execute_OperationCancelled_DoesNotRetryOrWrap()
    {
        int attempts = 0;

        Assert.Throws<OperationCanceledException>(() =>
            ApplicationDiscoveryRetry.Execute<object>(
                "Steam",
                () =>
                {
                    attempts++;
                    throw new OperationCanceledException();
                },
                attemptCount: 4,
                _ => { }
            )
        );

        Assert.Equal(1, attempts);
    }
}
