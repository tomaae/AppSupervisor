using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>Verifies Windows AppsFolder app URIs and update-safe package identity validation.</summary>
public sealed class StoreLaunchConfigTests
{
    private const string FamilyName = "96ba052f_4s4k90pjvq32p";
    private const string ApplicationId = "App";
    private const string AppUri = "shell:AppsFolder\\96ba052f_4s4k90pjvq32p!App";

    /// <summary>Confirms a complete package-backed AppsFolder helper configuration is valid.</summary>
    [Fact]
    public void Validate_CompleteStoreApplication_Succeeds()
    {
        SupervisorProfileConfig profile = CreateProfile(includeCompleteIdentity: true);

        ConfigValidator.Validate([profile]);

        Assert.Equal(AppUri, profile.Applications[0].AppUri);
    }

    /// <summary>Confirms partial package metadata cannot silently disable update-safe path resolution.</summary>
    [Fact]
    public void Validate_IncompleteStoreIdentity_ThrowsValidationError()
    {
        SupervisorProfileConfig profile = CreateProfile(includeCompleteIdentity: false);

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([profile])
        );

        Assert.Contains("identity fields must be configured together", exception.Message);
    }

    /// <summary>Confirms AppsFolder targets are translated into Explorer shell launch requests.</summary>
    [Fact]
    public void CreateStartInfo_StoreApplication_UsesExplorerAppsFolderTarget()
    {
        ManagedApplicationConfig application = CreateProfile(includeCompleteIdentity: true)
            .Applications[0];

        System.Diagnostics.ProcessStartInfo startInfo =
            ApplicationUri.CreateStartInfo(application);

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe"
            ),
            startInfo.FileName
        );
        Assert.Equal(AppUri, startInfo.Arguments);
    }

    /// <summary>Creates an enabled helper with either complete or deliberately incomplete package identity.</summary>
    /// <param name="includeCompleteIdentity">Whether every package identity field should be populated.</param>
    /// <returns>The launch-validation supervisor profile.</returns>
    private static SupervisorProfileConfig CreateProfile(bool includeCompleteIdentity)
    {
        return new SupervisorProfileConfig
        {
            Name = "Store launch test",
            MonitorProcess = "notepad.exe",
            Applications =
            [
                new ManagedApplicationConfig
                {
                    Path = Environment.ProcessPath!,
                    AppUri = AppUri,
                    PackageFamilyName = FamilyName,
                    PackageApplicationId = includeCompleteIdentity ? ApplicationId : "",
                    PackageExecutable = "VRCFaceTracking.exe",
                    Notifications = new NotificationConfig { Target = [] }
                }
            ]
        };
    }
}
