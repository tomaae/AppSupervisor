using AppSupervisor.Configuration;

namespace AppSupervisor.Tests;

/// <summary>Verifies profile dependency identity, enabled-state, and acyclic graph rules.</summary>
public sealed class ProfileDependencyValidationTests
{
    [Fact]
    public void Validate_ValidDependencyChain_AcceptsConfiguration()
    {
        SupervisorProfileConfig first = CreateProfile("first", "First");
        SupervisorProfileConfig second = CreateProfile("second", "Second", first.ProfileId);
        SupervisorProfileConfig third = CreateProfile("third", "Third", second.ProfileId);

        ConfigValidator.Validate([third, first, second]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("self")]
    [InlineData("disabled")]
    public void Validate_InvalidDependency_ReportsSpecificFailure(string scenario)
    {
        SupervisorProfileConfig dependency = CreateProfile("dependency", "Dependency");
        SupervisorProfileConfig dependent = CreateProfile("dependent", "Dependent");

        string expected = scenario switch
        {
            "missing" => "missing dependencyProfileId",
            "self" => "cannot depend on itself",
            "disabled" => "cannot depend on disabled profile",
            _ => throw new InvalidOperationException()
        };
        dependent.DependencyProfileId = scenario switch
        {
            "missing" => "missing-profile",
            "self" => dependent.ProfileId,
            "disabled" => dependency.ProfileId,
            _ => ""
        };
        dependency.Enabled = scenario != "disabled";

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([dependency, dependent])
        );

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Validate_ProfileDependencyCycle_RejectsConfiguration()
    {
        SupervisorProfileConfig first = CreateProfile("first", "First", "second");
        SupervisorProfileConfig second = CreateProfile("second", "Second", "third");
        SupervisorProfileConfig third = CreateProfile("third", "Third", "first");

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Validate([first, second, third])
        );

        Assert.Contains("dependency cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("First -> Second -> Third -> First", exception.Message);
    }

    private static SupervisorProfileConfig CreateProfile(
        string id,
        string name,
        string dependencyProfileId = "") => new()
    {
        ProfileId = id,
        Name = name,
        DependencyProfileId = dependencyProfileId,
        MonitorProcess = $"{name}.exe"
    };
}
