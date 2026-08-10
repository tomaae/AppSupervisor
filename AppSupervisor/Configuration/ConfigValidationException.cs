namespace AppSupervisor.Configuration;

/// <summary>
/// Represents one or more semantic errors found in a deserialized configuration file.
/// </summary>
public sealed class ConfigValidationException : Exception
{
    /// <summary>
    /// Creates an exception containing a user-readable summary of every validation error.
    /// </summary>
    /// <param name="errors">The configuration errors to include in the exception message.</param>
    public ConfigValidationException(IEnumerable<string> errors)
        : base("Configuration is invalid:" + Environment.NewLine +
               string.Join(Environment.NewLine, errors.Select(error => $"- {error}")))
    {
    }
}
