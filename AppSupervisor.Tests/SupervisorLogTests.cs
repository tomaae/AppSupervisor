namespace AppSupervisor.Tests;

/// <summary>Verifies diagnostics remain local to the executable instance.</summary>
public sealed class SupervisorLogTests
{
    /// <summary>Confirms Info suppresses Trace while retaining routine and more severe records.</summary>
    [Theory]
    [InlineData(SupervisorLogLevel.Trace, SupervisorLogLevel.Info, false)]
    [InlineData(SupervisorLogLevel.Info, SupervisorLogLevel.Info, true)]
    [InlineData(SupervisorLogLevel.Warning, SupervisorLogLevel.Info, true)]
    [InlineData(SupervisorLogLevel.Error, SupervisorLogLevel.Info, true)]
    [InlineData(SupervisorLogLevel.Info, SupervisorLogLevel.Warning, false)]
    public void IsEnabled_UsesConfiguredMinimumSeverity(
        SupervisorLogLevel level,
        SupervisorLogLevel minimumLevel,
        bool expected)
    {
        Assert.Equal(expected, SupervisorLog.IsEnabled(level, minimumLevel));
    }

    /// <summary>Confirms log records use whole seconds, 24-hour time, and the UTC offset.</summary>
    [Fact]
    public void FormatTimestamp_OmitsFractionalSeconds()
    {
        var timestamp = new DateTimeOffset(
            2026,
            8,
            17,
            21,
            20,
            5,
            414,
            TimeSpan.FromHours(2)
        );

        Assert.Equal("2026-08-17T21:20:05+02:00", SupervisorLog.FormatTimestamp(timestamp));
    }

    /// <summary>Confirms the severity field is bracketed in a complete log record.</summary>
    [Fact]
    public void FormatRecord_BracketsSeverity()
    {
        var timestamp = new DateTimeOffset(
            2026,
            8,
            17,
            21,
            20,
            5,
            TimeSpan.FromHours(2)
        );

        string record = SupervisorLog.FormatRecord(
            timestamp,
            SupervisorLogLevel.Info,
            "Started."
        );

        Assert.Equal(
            $"2026-08-17T21:20:05+02:00 [INFO] Started." +
            $"{Environment.NewLine}{Environment.NewLine}",
            record
        );
    }

    /// <summary>Confirms every continuation line is indented beneath its owning record.</summary>
    [Fact]
    public void FormatRecord_MultilineMessage_IndentsContinuationLines()
    {
        var timestamp = new DateTimeOffset(
            2026,
            8,
            17,
            21,
            20,
            5,
            TimeSpan.FromHours(2)
        );

        string record = SupervisorLog.FormatRecord(
            timestamp,
            SupervisorLogLevel.Error,
            "Discovery failed.\r\nTimeoutException: Timed out.\r\n   at Catalog.Load()"
        );

        Assert.Equal(
            $"2026-08-17T21:20:05+02:00 [ERROR] Discovery failed.{Environment.NewLine}" +
            $"\tTimeoutException: Timed out.{Environment.NewLine}" +
            $"\t   at Catalog.Load(){Environment.NewLine}{Environment.NewLine}",
            record
        );
    }

    /// <summary>Confirms every supported severity uses one canonical bracket label.</summary>
    [Theory]
    [InlineData((int)SupervisorLogLevel.Trace, "TRACE")]
    [InlineData((int)SupervisorLogLevel.Info, "INFO")]
    [InlineData((int)SupervisorLogLevel.Warning, "WARN")]
    [InlineData((int)SupervisorLogLevel.Error, "ERROR")]
    public void FormatRecord_Level_UsesCanonicalLabel(
        int levelValue,
        string expectedLabel)
    {
        var timestamp = new DateTimeOffset(
            2026,
            8,
            17,
            21,
            20,
            5,
            TimeSpan.FromHours(2)
        );

        string record = SupervisorLog.FormatRecord(
            timestamp,
            (SupervisorLogLevel)levelValue,
            "Message."
        );

        Assert.StartsWith(
            $"2026-08-17T21:20:05+02:00 [{expectedLabel}] Message.",
            record
        );
    }

    /// <summary>Confirms wrapped lines fit Lister's UTF-8 and default-tab display width.</summary>
    [Fact]
    public void FormatRecord_LongMessage_WrapsWithinListerWidth()
    {
        var timestamp = new DateTimeOffset(
            2026,
            8,
            17,
            21,
            20,
            5,
            TimeSpan.FromHours(2)
        );
        string message = string.Join(
            ' ',
            Enumerable.Repeat("configuration", 12)
                .Concat(Enumerable.Repeat("činnosť", 8))
        );

        string record = SupervisorLog.FormatRecord(
            timestamp,
            SupervisorLogLevel.Info,
            message
        );
        string[] physicalLines = record.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries
        );

        Assert.True(physicalLines.Length > 1);
        Assert.All(
            physicalLines,
            line => Assert.InRange(SupervisorLog.MeasureListerDisplayWidth(line), 1, 80)
        );
        Assert.All(physicalLines.Skip(1), line => Assert.StartsWith("\t", line));
    }

    /// <summary>Confirms quoted paths and oversized unspaced values are never split internally.</summary>
    [Fact]
    public void FormatRecord_QuotedPathAndLongToken_PreservesTokens()
    {
        var timestamp = new DateTimeOffset(
            2026,
            8,
            17,
            21,
            20,
            5,
            TimeSpan.FromHours(2)
        );
        const string quotedPath = "'C:\\Program Files\\Example Application\\Example.exe'";
        string longToken = new('x', 140);

        string record = SupervisorLog.FormatRecord(
            timestamp,
            SupervisorLogLevel.Info,
            $"Executable {quotedPath} token {longToken}"
        );

        Assert.Contains(quotedPath, record, StringComparison.Ordinal);
        Assert.Contains(longToken, record, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"'C:\\Program{Environment.NewLine}",
            record,
            StringComparison.Ordinal
        );
    }

    /// <summary>Confirms a first quoted value moves intact below a prefix it cannot fit beside.</summary>
    [Fact]
    public void FormatRecord_FirstQuotedPathDoesNotFit_MovesToContinuation()
    {
        var timestamp = new DateTimeOffset(
            2026,
            8,
            17,
            21,
            20,
            5,
            TimeSpan.FromHours(2)
        );
        string quotedPath = $"'C:\\{new string('p', 60)}.exe'";

        string record = SupervisorLog.FormatRecord(
            timestamp,
            SupervisorLogLevel.Info,
            quotedPath
        );
        string[] physicalLines = record.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries
        );

        Assert.Equal(2, physicalLines.Length);
        Assert.Equal("2026-08-17T21:20:05+02:00 [INFO]", physicalLines[0]);
        Assert.Equal($"\t{quotedPath}", physicalLines[1]);
        Assert.All(
            physicalLines,
            line => Assert.InRange(SupervisorLog.MeasureListerDisplayWidth(line), 1, 80)
        );
    }

    /// <summary>Confirms the timestamped log is stored beside config.json.</summary>
    [Fact]
    public void PathName_UsesApplicationBaseDirectoryAndSessionName()
    {
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            Path.GetDirectoryName(Path.GetFullPath(SupervisorLog.PathName))
        );
        Assert.Matches(
            "^AppSupervisor_[0-9]{6}-[0-9]{6}\\.log$",
            Path.GetFileName(SupervisorLog.PathName)
        );
    }

    /// <summary>Confirms two launches in the same second still receive different log names.</summary>
    [Fact]
    public void CreateSessionPath_ExistingTimestamp_AdvancesOneSecond()
    {
        string root = CreateTemporaryDirectory();

        try
        {
            var startedAt = new DateTimeOffset(2026, 8, 17, 21, 34, 56, TimeSpan.FromHours(2));
            string firstPath = SupervisorLog.CreateSessionPath(root, startedAt);
            File.WriteAllText(firstPath, "first");

            string secondPath = SupervisorLog.CreateSessionPath(root, startedAt);

            Assert.Equal("AppSupervisor_260817-213457.log", Path.GetFileName(secondPath));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>Confirms expired timestamped and legacy logs are removed after five days.</summary>
    [Fact]
    public void DeleteExpiredLogs_RemovesExpiredSessionAndLegacyLogs()
    {
        string root = CreateTemporaryDirectory();

        try
        {
            DateTime cutoffUtc = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);
            string expired = Path.Combine(root, "AppSupervisor_260811-090000.log");
            string retained = Path.Combine(root, "AppSupervisor_260816-090000.log");
            string legacy = Path.Combine(root, "AppSupervisor.log");
            File.WriteAllText(expired, "expired");
            File.WriteAllText(retained, "retained");
            File.WriteAllText(legacy, "legacy");
            File.SetLastWriteTimeUtc(expired, cutoffUtc.AddMinutes(-1));
            File.SetLastWriteTimeUtc(retained, cutoffUtc.AddMinutes(1));
            File.SetLastWriteTimeUtc(legacy, cutoffUtc.AddDays(-30));

            SupervisorLog.DeleteExpiredLogs(root, cutoffUtc);

            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(retained));
            Assert.False(File.Exists(legacy));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>Creates one isolated directory for log lifecycle tests.</summary>
    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"AppSupervisor.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes only the isolated directory created by this test.</summary>
    private static void DeleteTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string expectedPrefix = Path.GetFullPath(Path.GetTempPath());

        if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("AppSupervisor.Tests.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Temporary test path validation failed.");
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
