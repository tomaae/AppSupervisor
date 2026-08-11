namespace AppSupervisor.Tests;

/// <summary>
/// Protects the release packaging script from copying a developer's personal configuration into delivery artifacts.
/// </summary>
public sealed class PublishScriptSecurityTests
{
    /// <summary>Confirms packaging always creates and audits an empty configuration instead of referencing the local file.</summary>
    [Fact]
    public void Script_PersonalConfigurationNeverCopiedIntoPackage()
    {
        string script = File.ReadAllText(FindPublishScript());

        Assert.DoesNotContain("$sourceConfigPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"AppSupervisor\config.json",
            script,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.Contains("$emptyPackagedConfiguration", script, StringComparison.Ordinal);
        Assert.Contains(
            "The packaged config.json is not the required empty configuration.",
            script,
            StringComparison.Ordinal
        );
    }

    /// <summary>Finds Publish.ps1 by walking from the test output directory to the repository root.</summary>
    /// <returns>The full path to the repository packaging script.</returns>
    private static string FindPublishScript()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "Publish.ps1");

            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Publish.ps1 could not be found from the test output directory.");
    }
}
