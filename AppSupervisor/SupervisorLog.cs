using System.Text;

namespace AppSupervisor;

/// <summary>Defines the stable severity labels written by the diagnostic logger.</summary>
public enum SupervisorLogLevel
{
    Trace,
    Info,
    Warning,
    Error
}

/// <summary>
/// Writes best-effort diagnostic records for failures that cannot safely display a shutdown dialog.
/// </summary>
internal static class SupervisorLog
{
    private const int ListerTabWidth = 8;
    private const int MaximumLineLength = 80;
    private const int RetentionDays = 5;
    private static readonly object SyncRoot = new();
    private static readonly string SessionPath = CreateSessionPath(
        AppContext.BaseDirectory,
        DateTimeOffset.Now
    );
    private static int _minimumLevel = (int)SupervisorLogLevel.Info;
    private static bool _sessionInitialized;

    /// <summary>Gets this process run's instance-local diagnostic log path.</summary>
    public static string PathName => SessionPath;

    /// <summary>Appends one timestamped lifecycle record without allowing logging failures to escape.</summary>
    /// <param name="message">The diagnostic lifecycle detail.</param>
    public static void WriteInformation(string message)
    {
        WriteRecord(SupervisorLogLevel.Info, message);
    }

    /// <summary>Appends one detailed execution-flow record without allowing logging failures to escape.</summary>
    /// <param name="message">The fine-grained diagnostic detail.</param>
    public static void WriteTrace(string message)
    {
        WriteRecord(SupervisorLogLevel.Trace, message);
    }

    /// <summary>Appends one recoverable-problem record without allowing logging failures to escape.</summary>
    /// <param name="message">The diagnostic warning detail.</param>
    public static void WriteWarning(string message)
    {
        WriteRecord(SupervisorLogLevel.Warning, message);
    }

    /// <summary>Appends one timestamped exception without allowing logging failures to escape.</summary>
    public static void WriteError(string message, Exception exception)
    {
        WriteRecord(
            SupervisorLogLevel.Error,
            $"{message}{Environment.NewLine}{exception}"
        );
    }

    /// <summary>Appends one formatted diagnostic record under the shared writer lock.</summary>
    /// <param name="level">The diagnostic severity.</param>
    /// <param name="message">The complete record body.</param>
    private static void WriteRecord(SupervisorLogLevel level, string message)
    {
        if (!IsEnabled(level, (SupervisorLogLevel)Volatile.Read(ref _minimumLevel)))
            return;

        try
        {
            lock (SyncRoot)
            {
                string path = PathName;
                string directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);

                if (!_sessionInitialized)
                {
                    DeleteExpiredLogs(
                        directory,
                        DateTime.UtcNow.AddDays(-RetentionDays)
                    );
                    _sessionInitialized = true;
                }

                File.AppendAllText(
                    path,
                    FormatRecord(DateTimeOffset.Now, level, message)
                );
            }
        }
        catch
        {
            // Logging must never interfere with supervision or shutdown.
        }
    }

    /// <summary>Applies the minimum severity retained by subsequent log writes.</summary>
    /// <param name="level">The configured minimum log severity.</param>
    internal static void SetMinimumLevel(SupervisorLogLevel level)
    {
        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(level));

        Volatile.Write(ref _minimumLevel, (int)level);
    }

    /// <summary>Determines whether a record meets one configured minimum severity.</summary>
    internal static bool IsEnabled(
        SupervisorLogLevel level,
        SupervisorLogLevel minimumLevel) => level >= minimumLevel;

    /// <summary>Formats one local log timestamp without unnecessary fractional seconds.</summary>
    /// <param name="timestamp">The offset-aware timestamp to format.</param>
    /// <returns>An ISO 8601 timestamp through whole-second precision.</returns>
    internal static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToString(
            "yyyy-MM-dd'T'HH:mm:sszzz",
            System.Globalization.CultureInfo.InvariantCulture
        );

    /// <summary>Formats one complete log record with a stable severity field and indented continuation lines.</summary>
    /// <param name="timestamp">The offset-aware record timestamp.</param>
    /// <param name="level">The record severity.</param>
    /// <param name="message">The record content.</param>
    /// <returns>The record text including its trailing separator.</returns>
    internal static string FormatRecord(
        DateTimeOffset timestamp,
        SupervisorLogLevel level,
        string message)
    {
        string normalizedMessage = message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');
        string firstPrefix = $"{FormatTimestamp(timestamp)} [{FormatLevel(level)}] ";
        var physicalLines = new List<string>();

        foreach (string logicalLine in normalizedMessage.Split('\n'))
        {
            AppendWrappedLine(
                physicalLines,
                logicalLine,
                physicalLines.Count == 0 ? firstPrefix : "\t"
            );
        }

        return string.Join(Environment.NewLine, physicalLines) +
            $"{Environment.NewLine}{Environment.NewLine}";
    }

    /// <summary>Wraps one logical message line without splitting quoted or unspaced tokens.</summary>
    private static void AppendWrappedLine(
        ICollection<string> destination,
        string logicalLine,
        string initialPrefix)
    {
        int indentationLength = 0;

        while (indentationLength < logicalLine.Length &&
            char.IsWhiteSpace(logicalLine[indentationLength]))
        {
            indentationLength++;
        }

        string indentation = logicalLine[..indentationLength];
        IReadOnlyList<string> tokens = Tokenize(logicalLine[indentationLength..]);

        if (tokens.Count == 0)
        {
            destination.Add(initialPrefix + indentation);
            return;
        }

        string prefix = initialPrefix;
        var line = new StringBuilder(prefix + indentation);
        int contentStart = line.Length;
        int displayWidth = MeasureListerDisplayWidth(line.ToString());

        foreach (string token in tokens)
        {
            bool hasContent = line.Length > contentStart;
            int tokenWidth = Encoding.UTF8.GetByteCount(token);
            int requiredWidth = displayWidth + (hasContent ? 1 : 0) + tokenWidth;

            if (!hasContent &&
                requiredWidth > MaximumLineLength &&
                prefix != "\t" &&
                MeasureListerDisplayWidth("\t" + indentation) + tokenWidth <=
                    MaximumLineLength)
            {
                destination.Add(line.ToString().TrimEnd());
                prefix = "\t";
                line.Clear();
                line.Append(prefix);
                line.Append(indentation);
                contentStart = line.Length;
                displayWidth = MeasureListerDisplayWidth(line.ToString());
            }

            if (hasContent && requiredWidth > MaximumLineLength)
            {
                destination.Add(line.ToString());
                prefix = "\t";
                line.Clear();
                line.Append(prefix);
                line.Append(indentation);
                contentStart = line.Length;
                displayWidth = MeasureListerDisplayWidth(line.ToString());
                hasContent = false;
            }

            if (hasContent)
            {
                line.Append(' ');
                displayWidth++;
            }

            line.Append(token);
            displayWidth += tokenWidth;
        }

        destination.Add(line.ToString());
    }

    /// <summary>Splits text at whitespace while keeping single- or double-quoted values intact.</summary>
    private static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';

        foreach (char character in text)
        {
            if (quote == '\0' && char.IsWhiteSpace(character))
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }

                continue;
            }

            if (quote == '\0' && token.Length == 0 && character is '\'' or '"')
                quote = character;
            else if (quote == character)
                quote = '\0';

            token.Append(character);
        }

        if (token.Length > 0)
            tokens.Add(token.ToString());

        return tokens;
    }

    /// <summary>Measures one line as Lister displays UTF-8 bytes with eight-column tab stops.</summary>
    internal static int MeasureListerDisplayWidth(string text)
    {
        int displayWidth = 0;
        int segmentStart = 0;

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '\t')
                continue;

            displayWidth += Encoding.UTF8.GetByteCount(text.AsSpan(segmentStart, index - segmentStart));
            displayWidth += ListerTabWidth - displayWidth % ListerTabWidth;
            segmentStart = index + 1;
        }

        displayWidth += Encoding.UTF8.GetByteCount(text.AsSpan(segmentStart));
        return displayWidth;
    }

    /// <summary>Maps one severity to its canonical bracket label.</summary>
    private static string FormatLevel(SupervisorLogLevel level) => level switch
    {
        SupervisorLogLevel.Trace => "TRACE",
        SupervisorLogLevel.Info => "INFO",
        SupervisorLogLevel.Warning => "WARN",
        SupervisorLogLevel.Error => "ERROR",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    /// <summary>Allocates a timestamped path that does not reuse an existing session log.</summary>
    /// <param name="directory">The executable and configuration directory.</param>
    /// <param name="startedAt">The local process-session timestamp.</param>
    /// <returns>A unique timestamped log path.</returns>
    internal static string CreateSessionPath(string directory, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        for (int offsetSeconds = 0; offsetSeconds < 86_400; offsetSeconds++)
        {
            DateTimeOffset candidateTime = startedAt.AddSeconds(offsetSeconds);
            string path = Path.Combine(
                directory,
                $"AppSupervisor_{candidateTime:yyMMdd-HHmmss}.log"
            );

            if (!File.Exists(path))
                return path;
        }

        throw new IOException("A unique AppSupervisor session log name could not be allocated.");
    }

    /// <summary>Deletes current-format and legacy logs whose last write precedes the retention cutoff.</summary>
    /// <param name="directory">The executable and configuration directory.</param>
    /// <param name="cutoffUtc">The exclusive UTC retention cutoff.</param>
    internal static void DeleteExpiredLogs(string directory, DateTime cutoffUtc)
    {
        string[] paths;

        try
        {
            string[] sessionPaths = Directory.GetFiles(
                directory,
                "AppSupervisor_*.log",
                SearchOption.TopDirectoryOnly
            );
            string legacyPath = Path.Combine(directory, "AppSupervisor.log");
            paths = File.Exists(legacyPath)
                ? [.. sessionPaths, legacyPath]
                : sessionPaths;
        }
        catch
        {
            return;
        }

        foreach (string path in paths)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                    File.Delete(path);
            }
            catch
            {
                // One inaccessible historical log must not block the current session log.
            }
        }
    }
}
