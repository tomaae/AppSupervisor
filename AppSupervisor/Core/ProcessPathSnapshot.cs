using System.Diagnostics;

namespace AppSupervisor.Core;

/// <summary>
/// Shares a short-lived full-path process snapshot across resources in the same supervision cycle.
/// </summary>
internal static class ProcessPathSnapshot
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(250);
    private static readonly object SyncRoot = new();
    private static DateTime _capturedUtc;
    private static Dictionary<string, int[]> _processesByPath =
        new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns candidate identifiers whose executable path matched when the snapshot was captured.</summary>
    public static IReadOnlySet<int> FindCandidateProcessIds(string executablePath)
    {
        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new HashSet<int>();
        }

        lock (SyncRoot)
        {
            if (DateTime.UtcNow - _capturedUtc >= Lifetime)
                Capture();

            return _processesByPath.TryGetValue(fullPath, out int[]? processIds)
                ? processIds.ToHashSet()
                : new HashSet<int>();
        }
    }

    /// <summary>Rebuilds the cached path index while disposing every enumeration wrapper.</summary>
    private static void Capture()
    {
        var mutable = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string? processPath = process.MainModule?.FileName;

                if (processPath is null)
                    continue;

                string fullPath = Path.GetFullPath(processPath);

                if (!mutable.TryGetValue(fullPath, out List<int>? processIds))
                {
                    processIds = [];
                    mutable.Add(fullPath, processIds);
                }

                processIds.Add(process.Id);
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

        _processesByPath = mutable.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _capturedUtc = DateTime.UtcNow;
    }
}
