namespace AppSupervisor.Tests;

/// <summary>
/// Protects release artifacts from containing either personal or generated configuration files.
/// </summary>
public sealed class PublishScriptSecurityTests
{
    /// <summary>Confirms packaging rejects every configuration file instead of referencing or generating one.</summary>
    [Fact]
    public void Script_ConfigurationFileExcludedFromPackage()
    {
        string script = File.ReadAllText(FindPublishScript());

        Assert.DoesNotContain("$sourceConfigPath", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"AppSupervisor\config.json",
            script,
            StringComparison.OrdinalIgnoreCase
        );
        Assert.DoesNotContain("$emptyPackagedConfiguration", script, StringComparison.Ordinal);
        Assert.Contains("$forbiddenPackagedConfigPath", script, StringComparison.Ordinal);
        Assert.Contains(
            "Release packages must not contain config.json.",
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
