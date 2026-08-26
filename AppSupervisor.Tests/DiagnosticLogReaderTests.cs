using AppSupervisor.ConfigurationUI;
using System.Text;

namespace AppSupervisor.Tests;

/// <summary>Verifies tolerant parsing and shared-read session log lifecycle behavior.</summary>
public sealed class DiagnosticLogReaderTests
{
    /// <summary>Confirms continuation lines remain attached to one record with their message-owned indentation.</summary>
    [Fact]
    public void Parse_MultilineAndAdjacentRecords_PreservesRecordBodies()
    {
        const string content =
            "2026-08-26T12:10:00+02:00 [ERROR] Discovery failed.\r\n" +
            "\tTimeoutException: Timed out.\r\n" +
            "\t   at Catalog.Load()\r\n" +
            "2026-08-26T12:10:01+02:00 [INFO] Recovered.\r\n\r\n";

        IReadOnlyList<DiagnosticLogRecord> records = DiagnosticLogReader.Parse(content);

        Assert.Collection(
            records,
            record =>
            {
                Assert.Equal(new DateTimeOffset(2026, 8, 26, 12, 10, 0, TimeSpan.FromHours(2)),
                    record.Timestamp);
                Assert.Equal("ERROR", record.Level);
                Assert.Equal("Discovery failed.", record.Message);
                Assert.Equal(
                    $"TimeoutException: Timed out.{Environment.NewLine}   at Catalog.Load()",
                    record.Detail
                );
                Assert.False(record.IsMalformed);
            },
            record =>
            {
                Assert.Equal("INFO", record.Level);
                Assert.Equal("Recovered.", record.Message);
                Assert.Equal("", record.Detail);
                Assert.False(record.IsMalformed);
            }
        );
    }

    /// <summary>Confirms bad paragraphs and unknown levels remain visible without hiding later valid records.</summary>
    [Fact]
    public void Parse_MalformedAndPartialContent_ContainsDamageAndContinues()
    {
        const string content =
            "unrecognized legacy preamble\n" +
            "second preamble line\n\n" +
            "2026-08-26T12:10:00+02:00 [NOTICE] Unknown level.\n" +
            "\tadditional context\n" +
            "2026-08-26T12:10:01+02:00 [WARN] Partial trailing record";

        IReadOnlyList<DiagnosticLogRecord> records = DiagnosticLogReader.Parse(content);

        Assert.Collection(
            records,
            record =>
            {
                Assert.Null(record.Timestamp);
                Assert.Equal("MALFORMED", record.Level);
                Assert.Equal("unrecognized legacy preamble", record.Message);
                Assert.Equal("second preamble line", record.Detail);
                Assert.True(record.IsMalformed);
            },
            record =>
            {
                Assert.Equal("NOTICE", record.Level);
                Assert.Equal("Unknown level.", record.Message);
                Assert.Equal("additional context", record.Detail);
                Assert.True(record.IsMalformed);
            },
            record =>
            {
                Assert.Equal("WARN", record.Level);
                Assert.Equal("Partial trailing record", record.Message);
                Assert.False(record.IsMalformed);
            }
        );
    }

    /// <summary>Confirms discovery includes legacy logs, ignores unrelated files, and orders newest first.</summary>
    [Fact]
    public void DiscoverSessions_AvailableFiles_ReturnsNewestCurrentAndLegacyLogs()
    {
        string root = CreateTemporaryDirectory();

        try
        {
            string older = Path.Combine(root, "AppSupervisor_260826-100000.log");
            string newer = Path.Combine(root, "AppSupervisor_260826-110000.log");
            string legacy = Path.Combine(root, "AppSupervisor.log");
            File.WriteAllText(older, "older");
            File.WriteAllText(newer, "newer");
            File.WriteAllText(legacy, "legacy");
            File.WriteAllText(Path.Combine(root, "other.log"), "unrelated");
            DateTime baseline = new(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(older, baseline);
            File.SetLastWriteTimeUtc(legacy, baseline.AddMinutes(1));
            File.SetLastWriteTimeUtc(newer, baseline.AddMinutes(2));

            DiagnosticLogDiscoveryResult result = DiagnosticLogReader.DiscoverSessions(root);

            Assert.Null(result.Warning);
            Assert.Equal(
                ["AppSupervisor_260826-110000.log", "AppSupervisor.log",
                    "AppSupervisor_260826-100000.log"],
                result.Sessions.Select(session => session.FileName)
            );
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>Confirms a growing writer can remain open while consecutive reads observe new complete records.</summary>
    [Fact]
    public async Task ReadAsync_GrowingSharedFile_ObservesAppendedRecord()
    {
        string root = CreateTemporaryDirectory();
        string path = Path.Combine(root, "AppSupervisor_260826-120000.log");

        try
        {
            await using var writerStream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                4096,
                FileOptions.Asynchronous
            );
            await using var writer = new StreamWriter(
                writerStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024,
                leaveOpen: true
            );
            await writer.WriteAsync(
                "2026-08-26T12:00:00+02:00 [INFO] Started.\r\n\r\n"
            );
            await writer.FlushAsync();

            DiagnosticLogReadResult first = await DiagnosticLogReader.ReadAsync(
                path,
                CancellationToken.None
            );

            await writer.WriteAsync(
                "2026-08-26T12:00:01+02:00 [ERROR] Failed.\r\n" +
                "\tException: boom\r\n"
            );
            await writer.FlushAsync();
            DiagnosticLogReadResult second = await DiagnosticLogReader.ReadAsync(
                path,
                CancellationToken.None
            );

            Assert.Single(first.Records);
            Assert.Equal(2, second.Records.Count);
            Assert.Equal("Failed.", second.Records[1].Message);
            Assert.Equal("Exception: boom", second.Records[1].Detail);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>Confirms the viewer caps row materialization while retaining the newest records.</summary>
    [Fact]
    public async Task ReadAsync_ManyRecords_OmitsOldestRowsAndReportsCount()
    {
        string root = CreateTemporaryDirectory();
        string path = Path.Combine(root, "AppSupervisor_260826-130000.log");

        try
        {
            var content = new StringBuilder();
            int totalRecords = DiagnosticLogReader.MaximumDisplayedRecords + 5;
            for (int index = 0; index < totalRecords; index++)
            {
                content.Append("2026-08-26T13:00:00+02:00 [INFO] Record ");
                content.Append(index);
                content.Append(".\r\n\r\n");
            }
            File.WriteAllText(path, content.ToString());

            DiagnosticLogReadResult result = await DiagnosticLogReader.ReadAsync(
                path,
                CancellationToken.None
            );

            Assert.False(result.WasByteLimited);
            Assert.Equal(5, result.OmittedRecordCount);
            Assert.Equal(DiagnosticLogReader.MaximumDisplayedRecords, result.Records.Count);
            Assert.Equal("Record 5.", result.Records[0].Message);
            Assert.Equal($"Record {totalRecords - 1}.", result.Records[^1].Message);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    /// <summary>Creates one isolated directory for diagnostic log reader tests.</summary>
    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.DiagnosticLogTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Deletes only this test class's isolated directories.</summary>
    private static void DeleteTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string expectedPrefix = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith(
                "AppSupervisor.DiagnosticLogTests-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Temporary diagnostic-log test path validation failed.");
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
