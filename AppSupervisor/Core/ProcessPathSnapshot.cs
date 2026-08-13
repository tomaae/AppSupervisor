using System.Diagnostics;
using System.Runtime.InteropServices;

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
    private static Dictionary<int, int> _parentProcessIds = [];

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

    /// <summary>Returns the matching process identifiers that are not descendants of another matching process.</summary>
    /// <param name="processIds">Every process whose executable identity matched the managed application.</param>
    /// <returns>The independent application roots represented by the matching process set.</returns>
    public static IReadOnlySet<int> FindIndependentRootProcessIds(
        IReadOnlyCollection<int> processIds)
    {
        lock (SyncRoot)
        {
            if (DateTime.UtcNow - _capturedUtc >= Lifetime)
                Capture();

            return FindIndependentRootProcessIds(processIds, _parentProcessIds);
        }
    }

    /// <summary>Groups a matching process set by ancestry while tolerating missing or stale parent records.</summary>
    /// <param name="processIds">Every process whose executable identity matched.</param>
    /// <param name="parentProcessIds">The process-to-parent snapshot captured from Windows.</param>
    /// <returns>The process identifiers that represent independent application instances.</returns>
    internal static IReadOnlySet<int> FindIndependentRootProcessIds(
        IReadOnlyCollection<int> processIds,
        IReadOnlyDictionary<int, int> parentProcessIds)
    {
        var matchingIds = processIds.ToHashSet();
        var roots = new HashSet<int>();

        foreach (int processId in matchingIds)
        {
            int currentId = processId;
            var visited = new HashSet<int> { currentId };

            while (parentProcessIds.TryGetValue(currentId, out int parentId))
            {
                if (!matchingIds.Contains(parentId))
                {
                    roots.Add(currentId);
                    break;
                }

                if (!visited.Add(parentId))
                {
                    roots.Add(visited.Min());
                    break;
                }

                currentId = parentId;
            }
        }

        if (roots.Count == 0 && matchingIds.Count > 0)
            roots.Add(matchingIds.Min());

        return roots;
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

        Dictionary<int, int> parentProcessIds = CaptureParentProcessIds();
        _processesByPath = mutable.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _parentProcessIds = parentProcessIds;
        _capturedUtc = DateTime.UtcNow;
    }

    /// <summary>Captures Windows process-parent relationships without depending on slow management queries.</summary>
    /// <returns>A process-to-parent map from the same general observation period as the path snapshot.</returns>
    private static Dictionary<int, int> CaptureParentProcessIds()
    {
        var parentProcessIds = new Dictionary<int, int>();
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.TH32CS_SNAPPROCESS,
            0
        );

        if (snapshot == NativeMethods.INVALID_HANDLE_VALUE)
            return parentProcessIds;

        try
        {
            var entry = new NativeMethods.PROCESSENTRY32
            {
                dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>()
            };

            if (!NativeMethods.Process32First(snapshot, ref entry))
                return parentProcessIds;

            do
            {
                parentProcessIds[(int)entry.th32ProcessID] =
                    (int)entry.th32ParentProcessID;
                entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>();
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));

            return parentProcessIds;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }
}
