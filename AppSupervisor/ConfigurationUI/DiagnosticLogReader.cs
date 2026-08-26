using System.Buffers;
using System.Globalization;
using System.Text;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Describes one diagnostic log file that can be selected in the configuration editor.</summary>
internal sealed record DiagnosticLogSession(
    string Path,
    string FileName,
    DateTime LastWriteTimeUtc,
    long Length)
{
    /// <summary>Formats the session name, local modification time, and approximate size for selection.</summary>
    public override string ToString()
    {
        string size = Length switch
        {
            >= 1024 * 1024 => $"{Length / (1024d * 1024d):0.0} MB",
            >= 1024 => $"{Length / 1024d:0.0} KB",
            _ => $"{Length} B"
        };
        return $"{FileName} — {LastWriteTimeUtc.ToLocalTime():g} — {size}";
    }
}

/// <summary>Represents one parsed diagnostic record or one contained malformed paragraph.</summary>
internal sealed record DiagnosticLogRecord(
    DateTimeOffset? Timestamp,
    string Level,
    string Message,
    string Detail,
    bool IsMalformed)
{
    /// <summary>Gets a stable sortable timestamp string for the record grid.</summary>
    public string TimeText => Timestamp?.ToString(
        "yyyy-MM-dd HH:mm:ss zzz",
        CultureInfo.CurrentCulture
    ) ?? "—";

    /// <summary>Gets non-empty summary text for compact grid presentation.</summary>
    public string DisplayMessage => string.IsNullOrEmpty(Message)
        ? "(no message)"
        : Message;

    /// <summary>Gets the complete record body, retaining every continuation line.</summary>
    public string FullText => string.IsNullOrEmpty(Detail)
        ? Message
        : $"{Message}{Environment.NewLine}{Detail}";
}

/// <summary>Contains a best-effort session discovery result and any directory-level warning.</summary>
internal sealed record DiagnosticLogDiscoveryResult(
    IReadOnlyList<DiagnosticLogSession> Sessions,
    string? Warning);

/// <summary>Contains parsed records and information about protective viewer limits.</summary>
internal sealed record DiagnosticLogReadResult(
    IReadOnlyList<DiagnosticLogRecord> Records,
    bool WasByteLimited,
    int OmittedRecordCount);

/// <summary>Discovers, reads, and parses AppSupervisor diagnostic session logs without locking writers.</summary>
internal static class DiagnosticLogReader
{
    internal const int MaximumReadBytes = 16 * 1024 * 1024;
    internal const int MaximumDisplayedRecords = 10_000;
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:sszzz";

    /// <summary>Finds current-format and legacy logs, newest first, without exposing discovery failures.</summary>
    internal static DiagnosticLogDiscoveryResult DiscoverSessions(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        try
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string path in Directory.EnumerateFiles(
                directory,
                "AppSupervisor_*.log",
                SearchOption.TopDirectoryOnly))
            {
                paths.Add(path);
            }

            string legacyPath = System.IO.Path.Combine(directory, "AppSupervisor.log");
            if (File.Exists(legacyPath))
                paths.Add(legacyPath);

            var sessions = new List<DiagnosticLogSession>();
            foreach (string path in paths)
            {
                try
                {
                    var file = new FileInfo(path);
                    sessions.Add(new DiagnosticLogSession(
                        file.FullName,
                        file.Name,
                        file.LastWriteTimeUtc,
                        file.Length
                    ));
                }
                catch
                {
                    // A rotating or inaccessible file must not hide other readable sessions.
                }
            }

            return new DiagnosticLogDiscoveryResult(
                sessions
                    .OrderByDescending(session => session.LastWriteTimeUtc)
                    .ThenByDescending(session => session.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                null
            );
        }
        catch (DirectoryNotFoundException)
        {
            return new DiagnosticLogDiscoveryResult([], "The log directory does not exist.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiagnosticLogDiscoveryResult(
                [],
                $"Session logs could not be listed: {exception.Message}"
            );
        }
    }

    /// <summary>
    /// Reads the newest bounded portion of one growing log with delete sharing, then parses complete and partial records.
    /// </summary>
    internal static async Task<DiagnosticLogReadResult> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );

        long sourceLength = stream.Length;
        bool wasByteLimited = sourceLength > MaximumReadBytes;
        long startOffset = Math.Max(0, sourceLength - MaximumReadBytes);
        int requestedBytes = checked((int)Math.Min(sourceLength, MaximumReadBytes));
        stream.Seek(startOffset, SeekOrigin.Begin);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, requestedBytes));
        string content;
        try
        {
            int bytesRead = 0;
            while (bytesRead < requestedBytes)
            {
                int count = await stream.ReadAsync(
                    buffer.AsMemory(bytesRead, requestedBytes - bytesRead),
                    cancellationToken
                );
                if (count == 0)
                    break;
                bytesRead += count;
            }

            content = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (content.StartsWith('\uFEFF'))
            content = content[1..];
        if (wasByteLimited)
        {
            int firstLineEnd = content.IndexOf('\n');
            content = firstLineEnd < 0 ? "" : content[(firstLineEnd + 1)..];
        }

        IReadOnlyList<DiagnosticLogRecord> parsed = Parse(content);
        int omittedRecordCount = Math.Max(0, parsed.Count - MaximumDisplayedRecords);
        IReadOnlyList<DiagnosticLogRecord> displayed = omittedRecordCount == 0
            ? parsed
            : parsed.Skip(omittedRecordCount).ToArray();

        return new DiagnosticLogReadResult(
            displayed,
            wasByteLimited,
            omittedRecordCount
        );
    }

    /// <summary>Parses stable headers while grouping continuation lines and containing malformed text.</summary>
    internal static IReadOnlyList<DiagnosticLogRecord> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string normalized = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var records = new List<DiagnosticLogRecord>();
        RecordBuilder? current = null;

        foreach (string line in lines)
        {
            if (TryParseHeader(line, out DateTimeOffset timestamp, out string level,
                    out string message, out bool malformedHeader))
            {
                Flush(records, ref current);
                current = new RecordBuilder(timestamp, level, message, malformedHeader);
                continue;
            }

            if (line.Length == 0)
            {
                Flush(records, ref current);
                continue;
            }

            if (current is null)
            {
                current = new RecordBuilder(
                    timestamp: null,
                    level: "MALFORMED",
                    message: RemoveContinuationIndent(line),
                    isMalformed: true
                );
            }
            else
            {
                current.DetailLines.Add(RemoveContinuationIndent(line));
            }
        }

        Flush(records, ref current);
        return records;
    }

    /// <summary>Recognizes a timestamp and bracket field while retaining unknown severity labels as malformed records.</summary>
    private static bool TryParseHeader(
        string line,
        out DateTimeOffset timestamp,
        out string level,
        out string message,
        out bool malformed)
    {
        timestamp = default;
        level = "";
        message = "";
        malformed = false;

        if (line.Length < 29 || line[25] != ' ' || line[26] != '[' ||
            !DateTimeOffset.TryParseExact(
                line[..25],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out timestamp))
        {
            return false;
        }

        int closingBracket = line.IndexOf(']', 27);
        if (closingBracket < 28)
            return false;

        level = line[27..closingBracket];
        message = closingBracket + 1 >= line.Length
            ? ""
            : line[(closingBracket + 1)..].TrimStart(' ');
        malformed = level is not ("TRACE" or "INFO" or "WARN" or "ERROR");
        return true;
    }

    /// <summary>Removes exactly the writer-owned tab while retaining message-owned indentation.</summary>
    private static string RemoveContinuationIndent(string line) =>
        line.StartsWith('\t') ? line[1..] : line;

    /// <summary>Emits one in-progress record if present.</summary>
    private static void Flush(
        ICollection<DiagnosticLogRecord> destination,
        ref RecordBuilder? current)
    {
        if (current is null)
            return;

        destination.Add(new DiagnosticLogRecord(
            current.Timestamp,
            current.Level,
            current.Message,
            string.Join(Environment.NewLine, current.DetailLines),
            current.IsMalformed
        ));
        current = null;
    }

    /// <summary>Accumulates continuation lines until a separator or a new record header.</summary>
    private sealed class RecordBuilder(
        DateTimeOffset? timestamp,
        string level,
        string message,
        bool isMalformed)
    {
        public DateTimeOffset? Timestamp { get; } = timestamp;
        public string Level { get; } = level;
        public string Message { get; } = message;
        public bool IsMalformed { get; } = isMalformed;
        public List<string> DetailLines { get; } = [];
    }
}
