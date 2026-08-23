using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppSupervisor.Core;

/// <summary>
/// Owns the timer-facing process observation cache and coordinates one lifecycle transition per executable path.
/// </summary>
internal static class ProcessPathSnapshot
{
    private static readonly TimeSpan MaximumUnscopedLifetime = TimeSpan.FromSeconds(1);
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, LifecycleTransition> Transitions =
        new(StringComparer.OrdinalIgnoreCase);

    private static DateTime _cycleStartedUtc;
    private static long _cycleGeneration;
    private static bool _preferSharedSnapshot;
    private static bool _sharedSnapshotCaptured;
    private static HashSet<string> _requestedProcessNames =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, int[]> _processesByName =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, ExactPathObservation> _processesByPath =
        new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<int, int> _parentProcessIds = [];

    /// <summary>Starts a lazy cache epoch for one serialized timer pass.</summary>
    internal static void BeginCycle(bool preferSharedSnapshot)
    {
        lock (SyncRoot)
        {
            _cycleStartedUtc = DateTime.UtcNow;
            _cycleGeneration++;
            _preferSharedSnapshot = preferSharedSnapshot;
            _sharedSnapshotCaptured = false;
            _requestedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _processesByName = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
            _processesByPath = new Dictionary<string, ExactPathObservation>(StringComparer.OrdinalIgnoreCase);
            _parentProcessIds = [];
        }
    }

    /// <summary>Checks process-name presence, reusing the current timer-cycle cache.</summary>
    internal static bool IsProcessNameRunning(string processName)
        => FindProcessNameIds(processName).Count > 0;

    /// <summary>Returns process identifiers for one executable name from the current timer-cycle cache.</summary>
    internal static IReadOnlyList<int> FindProcessNameIds(string processName)
    {
        string normalizedName = NormalizeProcessName(processName);
        if (normalizedName.Length == 0)
            return [];

        lock (SyncRoot)
        {
            EnsureCurrentCycle();
            _requestedProcessNames.Add(normalizedName);

            if (_sharedSnapshotCaptured || _preferSharedSnapshot)
            {
                EnsureSharedSnapshot();

                if (_sharedSnapshotCaptured)
                {
                    return _processesByName.TryGetValue(normalizedName, out int[]? ids)
                        ? ids
                        : [];
                }
            }

            if (!_processesByName.TryGetValue(normalizedName, out int[]? processIds))
            {
                processIds = CaptureProcessIdsByName(normalizedName);
                _processesByName[normalizedName] = processIds;
            }

            return processIds;
        }
    }

    /// <summary>Returns exact-path process identifiers cached for the current timer pass.</summary>
    internal static IReadOnlySet<int> FindExactPathProcessIds(string executablePath)
    {
        return ObserveExactPath(executablePath).ProcessIds.ToHashSet();
    }

    /// <summary>Returns exact-path process identifiers and whether every same-name candidate was inspectable.</summary>
    internal static ExactPathObservation ObserveExactPath(string executablePath)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return new ExactPathObservation([], IsAuthoritative: false);

        lock (SyncRoot)
        {
            EnsureCurrentCycle();

            if (_processesByPath.TryGetValue(fullPath, out ExactPathObservation cached))
                return cached;

            string processName = NormalizeProcessName(fullPath);
            _requestedProcessNames.Add(processName);
            int[] candidates;

            if (_sharedSnapshotCaptured || _preferSharedSnapshot)
            {
                EnsureSharedSnapshot();

                if (_sharedSnapshotCaptured)
                {
                    candidates = _processesByName.TryGetValue(processName, out int[]? sharedIds)
                        ? sharedIds
                        : [];
                }
                else
                {
                    candidates = CaptureProcessIdsByName(processName);
                    _processesByName[processName] = candidates;
                }
            }
            else if (!_processesByName.TryGetValue(processName, out int[]? targetedCandidates))
            {
                candidates = CaptureProcessIdsByName(processName);
                _processesByName[processName] = candidates;
            }
            else
            {
                candidates = targetedCandidates;
            }

            ExactPathObservation observation = FilterExactPath(candidates, fullPath);
            _processesByPath[fullPath] = observation;
            return observation;
        }
    }

    /// <summary>Performs a targeted fresh exact-path lookup with an authoritative-result signal.</summary>
    internal static ExactPathObservation ObserveExactPathFresh(string executablePath)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return new ExactPathObservation([], IsAuthoritative: false);

        string processName = NormalizeProcessName(fullPath);
        int[] candidates = CaptureProcessIdsByName(processName);
        ExactPathObservation observation = FilterExactPath(candidates, fullPath);

        lock (SyncRoot)
        {
            EnsureCurrentCycle();
            _requestedProcessNames.Add(processName);
            _processesByName[processName] = candidates;
            _processesByPath[fullPath] = observation;
        }

        return observation;
    }

    /// <summary>Returns matching identifiers that are not descendants of another matching process.</summary>
    public static IReadOnlySet<int> FindIndependentRootProcessIds(
        IReadOnlyCollection<int> processIds)
    {
        lock (SyncRoot)
        {
            EnsureCurrentCycle();
            EnsureSharedSnapshot();
            return FindIndependentRootProcessIds(processIds, _parentProcessIds);
        }
    }

    /// <summary>Groups a matching process set by ancestry while tolerating missing parent records.</summary>
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

    /// <summary>Requests one globally serialized transition for an exact executable path.</summary>
    internal static void RequestTransition(
        string executablePath,
        object owner,
        ProcessLifecycleTransitionKind kind)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return;

        lock (SyncRoot)
        {
            if (!Transitions.TryGetValue(fullPath, out LifecycleTransition? transition))
            {
                Transitions.Add(fullPath, new LifecycleTransition(kind, owner));
                return;
            }

            LifecycleRequest lastRequest = transition.Following.Count > 0
                ? transition.Following.Last()
                : new LifecycleRequest(transition.Kind, transition.Owner);

            if (lastRequest.Kind == kind && ReferenceEquals(lastRequest.Owner, owner))
                return;

            transition.Following.Enqueue(new LifecycleRequest(kind, owner));
        }
    }

    /// <summary>Gets a transition only for the resource instance that currently owns it.</summary>
    internal static ProcessLifecycleTransitionKind? GetOwnedTransition(
        string executablePath,
        object owner)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return null;

        lock (SyncRoot)
        {
            return Transitions.TryGetValue(fullPath, out LifecycleTransition? transition) &&
                ReferenceEquals(transition.Owner, owner)
                    ? transition.Kind
                    : null;
        }
    }

    /// <summary>Gets the current path-scoped transition without inspecting the operating system.</summary>
    internal static ProcessLifecycleTransitionKind? GetTransition(string executablePath)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return null;

        lock (SyncRoot)
        {
            return Transitions.TryGetValue(fullPath, out LifecycleTransition? transition)
                ? transition.Kind
                : null;
        }
    }

    /// <summary>Gets whether any resource owns a transition for the executable.</summary>
    internal static bool HasTransition(string executablePath)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return false;

        lock (SyncRoot)
            return Transitions.ContainsKey(fullPath);
    }

    /// <summary>Gets whether the executable is deliberately being closed.</summary>
    internal static bool IsClosePending(string executablePath)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return false;

        lock (SyncRoot)
        {
            return Transitions.TryGetValue(fullPath, out LifecycleTransition? transition) &&
                (transition.Kind == ProcessLifecycleTransitionKind.Close ||
                    transition.Following.Any(request =>
                        request.Kind == ProcessLifecycleTransitionKind.Close));
        }
    }

    /// <summary>Gets the current serialized supervision-cycle identity.</summary>
    internal static long CurrentCycleGeneration
    {
        get
        {
            lock (SyncRoot)
            {
                EnsureCurrentCycle();
                return _cycleGeneration;
            }
        }
    }

    /// <summary>
    /// Recommends a shared snapshot for the next equivalent pass after at least three distinct
    /// process names were actually requested in the current pass.
    /// </summary>
    internal static bool ShouldPreferSharedSnapshotNextCycle
    {
        get
        {
            lock (SyncRoot)
                return ShouldPreferSharedSnapshot(_requestedProcessNames.Count);
        }
    }

    /// <summary>Chooses targeted lookups below the measured full-snapshot break-even point.</summary>
    internal static bool ShouldPreferSharedSnapshot(int distinctProcessNameCount) =>
        distinctProcessNameCount >= 3;

    /// <summary>Completes an owned transition and promotes its queued opposite only after success.</summary>
    internal static void CompleteTransition(
        string executablePath,
        object owner,
        bool succeeded)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return;

        lock (SyncRoot)
        {
            if (!Transitions.TryGetValue(fullPath, out LifecycleTransition? transition) ||
                !ReferenceEquals(transition.Owner, owner))
            {
                return;
            }

            Transitions.Remove(fullPath);

            if (succeeded && transition.Following.Count > 0)
            {
                LifecycleRequest following = transition.Following.Dequeue();
                var promoted = new LifecycleTransition(following.Kind, following.Owner);

                while (transition.Following.Count > 0)
                    promoted.Following.Enqueue(transition.Following.Dequeue());

                Transitions.Add(fullPath, promoted);
            }
        }
    }

    /// <summary>Removes queued requests owned by one resource without interrupting the current mutation.</summary>
    internal static void CancelQueuedTransitions(
        string executablePath,
        object owner,
        ProcessLifecycleTransitionKind kind)
    {
        string? fullPath = TryNormalizePath(executablePath);
        if (fullPath is null)
            return;

        lock (SyncRoot)
        {
            if (!Transitions.TryGetValue(fullPath, out LifecycleTransition? transition))
                return;

            LifecycleRequest[] retained = transition.Following
                .Where(request => request.Kind != kind ||
                    !ReferenceEquals(request.Owner, owner))
                .ToArray();
            transition.Following.Clear();

            foreach (LifecycleRequest request in retained)
                transition.Following.Enqueue(request);
        }
    }

    /// <summary>Releases transition ownership during configuration disposal without touching processes.</summary>
    internal static void ReleaseOwner(object owner)
    {
        lock (SyncRoot)
        {
            foreach (string path in Transitions
                .Where(pair => ReferenceEquals(pair.Value.Owner, owner) ||
                    pair.Value.Following.Any(request =>
                        ReferenceEquals(request.Owner, owner)))
                .Select(pair => pair.Key)
                .ToArray())
            {
                LifecycleTransition transition = Transitions[path];

                if (ReferenceEquals(transition.Owner, owner))
                {
                    Transitions.Remove(path);
                    continue;
                }

                LifecycleRequest[] retained = transition.Following
                    .Where(request => !ReferenceEquals(request.Owner, owner))
                    .ToArray();
                transition.Following.Clear();

                foreach (LifecycleRequest request in retained)
                    transition.Following.Enqueue(request);
            }
        }
    }

    private static void EnsureCurrentCycle()
    {
        if (_cycleStartedUtc != default &&
            DateTime.UtcNow - _cycleStartedUtc < MaximumUnscopedLifetime)
        {
            return;
        }

        _cycleStartedUtc = DateTime.UtcNow;
        _cycleGeneration++;
        _preferSharedSnapshot = false;
        _sharedSnapshotCaptured = false;
        _requestedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _processesByName = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        _processesByPath = new Dictionary<string, ExactPathObservation>(StringComparer.OrdinalIgnoreCase);
        _parentProcessIds = [];
    }

    /// <summary>Captures PID, parent PID, and executable filename without opening every process.</summary>
    private static void EnsureSharedSnapshot()
    {
        if (_sharedSnapshotCaptured)
            return;

        var mutableNames = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var parentProcessIds = new Dictionary<int, int>();
        IntPtr snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.TH32CS_SNAPPROCESS,
            0
        );

        if (snapshot != NativeMethods.INVALID_HANDLE_VALUE)
        {
            try
            {
                var entry = new NativeMethods.PROCESSENTRY32
                {
                    dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>()
                };

                if (!NativeMethods.Process32First(snapshot, ref entry))
                {
                    _preferSharedSnapshot = false;
                    return;
                }

                do
                {
                    int processId = (int)entry.th32ProcessID;
                    parentProcessIds[processId] = (int)entry.th32ParentProcessID;
                    string processName = NormalizeProcessName(entry.szExeFile);

                    if (processName.Length > 0)
                    {
                        if (!mutableNames.TryGetValue(processName, out List<int>? ids))
                        {
                            ids = [];
                            mutableNames.Add(processName, ids);
                        }

                        ids.Add(processId);
                    }

                    entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32>();
                }
                while (NativeMethods.Process32Next(snapshot, ref entry));
            }
            finally
            {
                NativeMethods.CloseHandle(snapshot);
            }
        }
        else
        {
            _preferSharedSnapshot = false;
            return;
        }

        _processesByName = mutableNames.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase
        );
        _parentProcessIds = parentProcessIds;
        _sharedSnapshotCaptured = true;
    }

    private static int[] CaptureProcessIdsByName(string normalizedName)
    {
        Process[] processes = Process.GetProcessesByName(normalizedName);

        try
        {
            return processes.Select(process => process.Id).ToArray();
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    private static ExactPathObservation FilterExactPath(
        IEnumerable<int> candidateIds,
        string expectedPath)
    {
        var matches = new List<int>();
        bool authoritative = true;

        foreach (int processId in candidateIds)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(processId);
                string? actualPath = process.MainModule?.FileName;

                if (actualPath is not null && string.Equals(
                    Path.GetFullPath(actualPath),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(processId);
                }
            }
            catch
            {
                // A mutation must not treat an uninspectable same-name candidate as confirmed absence.
                authoritative = false;
            }
            finally
            {
                process?.Dispose();
            }
        }

        return new ExactPathObservation(matches.ToArray(), authoritative);
    }

    private static string NormalizeProcessName(string pathOrName) =>
        Path.GetFileNameWithoutExtension(pathOrName).Trim();

    private static string? TryNormalizePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        try
        {
            return Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed class LifecycleTransition
    {
        public LifecycleTransition(ProcessLifecycleTransitionKind kind, object owner)
        {
            Kind = kind;
            Owner = owner;
        }

        public ProcessLifecycleTransitionKind Kind { get; }

        public object Owner { get; }

        public Queue<LifecycleRequest> Following { get; } = new();
    }

    private readonly record struct LifecycleRequest(
        ProcessLifecycleTransitionKind Kind,
        object Owner);
}

/// <summary>Identifies the single path-scoped process mutation currently being reconciled.</summary>
internal enum ProcessLifecycleTransitionKind
{
    Start,
    Close
}

/// <summary>Describes exact-path matches and whether absence was proven safely.</summary>
internal readonly record struct ExactPathObservation(
    int[] ProcessIds,
    bool IsAuthoritative);
