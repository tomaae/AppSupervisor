using System.Diagnostics;
using AppSupervisor.Core;

namespace AppSupervisor.Health;

/// <summary>Activates a health check only after a named process reaches a minimum continuous uptime.</summary>
internal sealed class ProcessUptimeCondition : IHealthCheckActivationCondition
{
    private readonly string _processName;
    private readonly TimeSpan _minimumUptime;
    private readonly Func<string, IReadOnlyList<int>> _processIdProvider;
    private readonly Func<int, DateTime?> _startTimeProvider;
    private readonly TimeProvider _timeProvider;

    private int? _qualifiedProcessId;

    /// <summary>Creates a production process-uptime prerequisite.</summary>
    public ProcessUptimeCondition(string processName, TimeSpan minimumUptime)
        : this(
            processName,
            minimumUptime,
            ProcessPathSnapshot.FindProcessNameIds,
            GetProcessStartTimeUtc,
            TimeProvider.System
        )
    {
    }

    /// <summary>Creates a process-uptime prerequisite with injectable observation sources.</summary>
    internal ProcessUptimeCondition(
        string processName,
        TimeSpan minimumUptime,
        Func<string, IReadOnlyList<int>> processIdProvider,
        Func<int, DateTime?> startTimeProvider,
        TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumUptime, TimeSpan.Zero);
        _processName = Path.GetFileNameWithoutExtension(processName);
        _minimumUptime = minimumUptime;
        _processIdProvider = processIdProvider;
        _startTimeProvider = startTimeProvider;
        _timeProvider = timeProvider;
    }

    /// <summary>Checks whether one current process instance has continuously existed for the required duration.</summary>
    public bool IsActive()
    {
        IReadOnlyList<int> processIds = _processIdProvider(_processName);

        if (_qualifiedProcessId is int qualifiedProcessId &&
            processIds.Contains(qualifiedProcessId))
        {
            return true;
        }

        _qualifiedProcessId = null;
        DateTime cutoffUtc = _timeProvider.GetUtcNow().UtcDateTime - _minimumUptime;

        foreach (int processId in processIds)
        {
            DateTime? startTimeUtc = _startTimeProvider(processId);

            if (startTimeUtc is not null && startTimeUtc <= cutoffUtc)
            {
                _qualifiedProcessId = processId;
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads one process start time conservatively when Windows still exposes that process.</summary>
    private static DateTime? GetProcessStartTimeUtc(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }
}
