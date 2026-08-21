using System.Diagnostics;

namespace AppSupervisor.Configuration;

/// <summary>Validates and translates direct, Steam, and Windows AppsFolder launch mechanisms.</summary>
internal static class ApplicationUri
{
    private const string AppsFolderPrefix = "shell:AppsFolder\\";

    /// <summary>Creates the process start request appropriate for the configured launch mechanism.</summary>
    /// <param name="configuration">The helper executable and optional app URI.</param>
    /// <returns>A shell-enabled process start request.</returns>
    public static ProcessStartInfo CreateStartInfo(ManagedApplicationConfig configuration)
    {
        string appUri = configuration.AppUri?.Trim() ?? "";
        string workingDirectory = GetWorkingDirectory(configuration.Path);

        if (string.IsNullOrWhiteSpace(appUri))
        {
            return new ProcessStartInfo
            {
                FileName = configuration.Path,
                Arguments = configuration.Arguments ?? "",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
        }

        if (IsAppsFolderUri(appUri))
        {
            string explorerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe"
            );

            return new ProcessStartInfo
            {
                FileName = explorerPath,
                Arguments = appUri,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = appUri,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true
        };
    }

    /// <summary>Returns the helper executable directory used by every supported launch mechanism.</summary>
    private static string GetWorkingDirectory(string executablePath)
    {
        string fullPath = Path.GetFullPath(executablePath);
        return Path.GetDirectoryName(fullPath) ?? Path.GetPathRoot(fullPath)!;
    }

    /// <summary>Checks whether an app URI is an exact positive Steam rungameid URI.</summary>
    /// <param name="appUri">The app URI to validate.</param>
    /// <returns><see langword="true"/> for a supported Steam app URI.</returns>
    public static bool IsSteamUri(string appUri)
    {
        return Uri.TryCreate(appUri.Trim(), UriKind.Absolute, out Uri? uri) &&
            string.Equals(uri.Scheme, "steam", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, "rungameid", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(uri.AbsolutePath.Trim('/'), out ulong appId) &&
            appId > 0;
    }

    /// <summary>Checks whether an app URI is a syntactically safe Explorer AppsFolder application identity.</summary>
    /// <param name="appUri">The app URI to validate.</param>
    /// <returns><see langword="true"/> for a supported AppsFolder app URI.</returns>
    public static bool IsAppsFolderUri(string appUri)
    {
        if (!appUri.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string identity = appUri[AppsFolderPrefix.Length..];
        int separatorIndex = identity.IndexOf('!');
        return separatorIndex > 0 &&
            separatorIndex < identity.Length - 1 &&
            identity.All(character =>
                char.IsLetterOrDigit(character) ||
                character is '.' or '-' or '_' or '!');
    }
}
