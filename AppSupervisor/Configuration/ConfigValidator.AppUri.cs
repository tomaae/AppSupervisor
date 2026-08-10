namespace AppSupervisor.Configuration;

/// <summary>Adds optional Steam and Windows AppsFolder app URI validation.</summary>
public static partial class ConfigValidator
{
    /// <summary>Validates one helper's optional app URI, arguments, and package identity.</summary>
    /// <param name="application">The helper configuration being validated.</param>
    /// <param name="applicationLabel">The user-readable helper identifier.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    private static void ValidateAppUri(
        ManagedApplicationConfig application,
        string applicationLabel,
        ICollection<string> errors)
    {
        string appUri = application.AppUri.Trim();
        bool hasAppUri = !string.IsNullOrWhiteSpace(appUri);

        if (hasAppUri &&
            !ApplicationUri.IsSteamUri(appUri) &&
            !ApplicationUri.IsAppsFolderUri(appUri))
        {
            errors.Add(
                $"{applicationLabel} appUri must use steam://rungameid/<positive app id> " +
                "or shell:AppsFolder\\<package family>!<application id>."
            );
        }

        if (hasAppUri && !string.IsNullOrWhiteSpace(application.Arguments))
            errors.Add($"{applicationLabel} cannot configure arguments together with appUri.");

        bool hasAnyPackageIdentity =
            !string.IsNullOrWhiteSpace(application.PackageFamilyName) ||
            !string.IsNullOrWhiteSpace(application.PackageApplicationId) ||
            !string.IsNullOrWhiteSpace(application.PackageExecutable);
        bool hasCompletePackageIdentity =
            !string.IsNullOrWhiteSpace(application.PackageFamilyName) &&
            !string.IsNullOrWhiteSpace(application.PackageApplicationId) &&
            !string.IsNullOrWhiteSpace(application.PackageExecutable);

        if (hasAnyPackageIdentity && !hasCompletePackageIdentity)
        {
            errors.Add(
                $"{applicationLabel} Windows package identity fields must be configured together."
            );
        }

        if (!hasCompletePackageIdentity)
            return;

        string expectedAppUri =
            $"shell:AppsFolder\\{application.PackageFamilyName}!{application.PackageApplicationId}";

        if (!string.Equals(
            appUri,
            expectedAppUri,
            StringComparison.OrdinalIgnoreCase
        ))
        {
            errors.Add(
                $"{applicationLabel} Windows package identity does not match its appUri."
            );
        }

        string[] executableSegments = application.PackageExecutable.Split(
            ['\\', '/'],
            StringSplitOptions.RemoveEmptyEntries
        );

        if (Path.IsPathRooted(application.PackageExecutable) ||
            executableSegments.Any(segment => segment == ".."))
        {
            errors.Add(
                $"{applicationLabel} packageExecutable must be a safe package-relative path."
            );
        }
    }
}
