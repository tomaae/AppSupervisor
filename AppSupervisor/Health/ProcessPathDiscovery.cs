using System.Diagnostics;

namespace AppSupervisor.Health;

/// <summary>
/// Resolves currently running process identifiers by authoritative full executable path for diagnostics and one-shot tests.
/// </summary>
internal static class ProcessPathDiscovery
{
    /// <summary>Finds every inspectable process whose executable path matches the supplied path.</summary>
    /// <param name="executablePath">The fully qualified executable identity.</param>
    /// <returns>A stable set of matching process identifiers.</returns>
    public static IReadOnlySet<int> FindRunningProcessIds(string executablePath)
    {
        var processIds = new HashSet<int>();

        if (string.IsNullOrWhiteSpace(executablePath))
            return processIds;

        string expectedPath;

        try
        {
            expectedPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return processIds;
        }

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string? processPath = process.MainModule?.FileName;

                if (processPath is not null && string.Equals(
                    Path.GetFullPath(processPath),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    processIds.Add(process.Id);
                }
            }
            catch
            {
                // Inaccessible processes cannot be authoritative path matches.
            }
            finally
            {
                process.Dispose();
            }
        }

        return processIds;
    }
}
