namespace AppSupervisor.Tests;

/// <summary>
/// Verifies elevated startup registration matching without changing the user's scheduled tasks.
/// </summary>
public sealed class StartupRegistrationTests
{
    private const string ExecutablePath = @"C:\Tools\AppSupervisor\AppSupervisor.exe";
    private const string WorkingDirectory = @"C:\Tools\AppSupervisor";
    private const string UserId = "S-1-5-21-1000";

    /// <summary>
    /// Confirms that the exact elevated current-user logon task is accepted.
    /// </summary>
    [Fact]
    public void IsMatchingRegistration_ExactElevatedTask_ReturnsTrue()
    {
        StartupTaskRegistration registration = CreateValidRegistration();

        Assert.True(StartupRegistration.IsMatchingRegistration(
            registration,
            ExecutablePath,
            UserId
        ));
    }

    /// <summary>
    /// Confirms that a task running without the highest privilege level is rejected.
    /// </summary>
    [Fact]
    public void IsMatchingRegistration_NonElevatedTask_ReturnsFalse()
    {
        StartupTaskRegistration registration = CreateValidRegistration() with
        {
            HighestPrivileges = false
        };

        Assert.False(StartupRegistration.IsMatchingRegistration(
            registration,
            ExecutablePath,
            UserId
        ));
    }

    /// <summary>
    /// Confirms that a task left behind by a different executable location is rejected and can be refreshed.
    /// </summary>
    [Fact]
    public void IsMatchingRegistration_StaleExecutablePath_ReturnsFalse()
    {
        StartupTaskRegistration registration = CreateValidRegistration() with
        {
            ExecutablePath = @"C:\Old\AppSupervisor.exe"
        };

        Assert.False(StartupRegistration.IsMatchingRegistration(
            registration,
            ExecutablePath,
            UserId
        ));
    }

    /// <summary>
    /// Confirms that querying a deliberately unique absent task returns no registration without changing Task Scheduler.
    /// </summary>
    [Fact]
    public void GetRegistration_MissingTask_ReturnsNull()
    {
        var scheduler = new WindowsStartupTaskScheduler(
            $"AppSupervisor.Tests.Missing.{Guid.NewGuid():N}"
        );

        Assert.Null(scheduler.GetRegistration());
    }

    /// <summary>
    /// Creates a registration satisfying every current startup requirement.
    /// </summary>
    /// <returns>A valid scheduled-task description used as a mutation baseline.</returns>
    private static StartupTaskRegistration CreateValidRegistration()
    {
        return new StartupTaskRegistration(
            ExecutablePath,
            WorkingDirectory,
            UserId,
            UserId,
            TaskEnabled: true,
            LogonTriggerEnabled: true,
            HighestPrivileges: true,
            IgnoreNewInstances: true,
            ActionCount: 1,
            TriggerCount: 1
        );
    }
}
