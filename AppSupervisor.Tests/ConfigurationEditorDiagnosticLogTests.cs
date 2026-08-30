using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies diagnostic log tab placement, browsing, and selection-triggered refresh behavior.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorDiagnosticLogTests
{
    /// <summary>Confirms the structured viewer follows Integrations and can switch between available sessions.</summary>
    [Fact]
    public void Constructor_DiagnosticLogsTab_FollowsIntegrationsAndBrowsesSessions()
    {
        string root = CreateTemporaryDirectory();
        string configPath = Path.Combine(root, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig());
        string olderPath = Path.Combine(root, "AppSupervisor_260826-100000.log");
        string newerPath = Path.Combine(root, "AppSupervisor_260826-110000.log");
        File.WriteAllText(
            olderPath,
            "2026-08-26T10:00:00+02:00 [INFO] Older session.\r\n\r\n"
        );
        File.WriteAllText(
            newerPath,
            "2026-08-26T11:00:00+02:00 [ERROR] Newer session failed.\r\n" +
            "\tTimeoutException: timed out\r\n" +
            "\t   at Session.Load()\r\n\r\n"
        );
        File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow.AddMinutes(-1));
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = CreateForm(configPath);
                    RunWithMessageLoop(form, async () =>
                    {
                        TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                        TabPage integrations = Assert.Single(
                            tabs.TabPages.Cast<TabPage>(),
                            page => page.Text == "Integrations"
                        );
                        TabPage logs = Assert.Single(
                            tabs.TabPages.Cast<TabPage>(),
                            page => page.Text == "Diagnostic logs"
                        );
                        Assert.Equal(tabs.TabPages.IndexOf(integrations) + 1,
                            tabs.TabPages.IndexOf(logs));

                        tabs.SelectedTab = logs;
                        ComboBox selector = FindNamedControl<ComboBox>(logs,
                            "DiagnosticLogSessionSelector");
                        DataGridView records = FindNamedControl<DataGridView>(logs,
                            "DiagnosticLogRecords");
                        RichTextBox detail = FindNamedControl<RichTextBox>(logs,
                            "DiagnosticLogDetail");
                        await WaitUntilAsync(() => selector.Items.Count == 2 && records.RowCount == 1 &&
                            detail.Text.Contains("Newer session failed.", StringComparison.Ordinal));

                        Assert.Equal(["Time", "Level", "Message"],
                            records.Columns.Cast<DataGridViewColumn>().Select(column => column.Name));
                        Assert.Contains("Newer session failed.", detail.Text,
                            StringComparison.Ordinal);
                        Assert.Contains(
                            "TimeoutException: timed out\n   at Session.Load()",
                            detail.Text.Replace("\r", "", StringComparison.Ordinal),
                            StringComparison.Ordinal
                        );

                        selector.SelectedIndex = 1;
                        await WaitUntilAsync(() => detail.Text.Contains("Older session.",
                            StringComparison.Ordinal));
                    });
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)),
                "Diagnostic log browsing UI test timed out.");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }

        Assert.Null(threadException);
    }

    /// <summary>Confirms returning to the tab observes growth and gracefully clears a rotated-away session.</summary>
    [Fact]
    public void SelectingDiagnosticLogsTab_ReloadsGrowingAndRemovedFiles()
    {
        string root = CreateTemporaryDirectory();
        string configPath = Path.Combine(root, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig());
        string logPath = Path.Combine(root, "AppSupervisor_260826-120000.log");
        File.WriteAllText(
            logPath,
            "2026-08-26T12:00:00+02:00 [INFO] First.\r\n\r\n"
        );
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = CreateForm(configPath);
                    RunWithMessageLoop(form, async () =>
                    {
                        TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                        TabPage profile = tabs.TabPages[0];
                        TabPage logs = Assert.Single(
                            tabs.TabPages.Cast<TabPage>(),
                            page => page.Text == "Diagnostic logs"
                        );
                        DataGridView records = FindNamedControl<DataGridView>(logs,
                            "DiagnosticLogRecords");
                        RichTextBox detail = FindNamedControl<RichTextBox>(logs,
                            "DiagnosticLogDetail");
                        Label status = FindNamedControl<Label>(logs, "DiagnosticLogStatus");

                        tabs.SelectedTab = logs;
                        await WaitUntilAsync(() => records.RowCount == 1, () => status.Text);
                        File.AppendAllText(
                            logPath,
                            "2026-08-26T12:00:01+02:00 [WARN] Appended.\r\n\r\n"
                        );
                        tabs.SelectedTab = profile;
                        tabs.SelectedTab = logs;
                        await WaitUntilAsync(() => records.RowCount == 2 &&
                            detail.Text.Contains("Appended.", StringComparison.Ordinal),
                            () => $"{status.Text}; rows={records.RowCount}; detail={detail.Text}");

                        // Detail must follow the new current cell, not the previous
                        // cell exposed while SelectionChanged is still firing.
                        records.CurrentCell = records.Rows[0].Cells[0];
                        Assert.Equal("First.", detail.Text);
                        records.CurrentCell = records.Rows[1].Cells[0];
                        Assert.Equal("Appended.", detail.Text);

                        File.Delete(logPath);
                        tabs.SelectedTab = profile;
                        tabs.SelectedTab = logs;
                        await WaitUntilAsync(() => records.RowCount == 0 &&
                            status.Text.Contains("No AppSupervisor session logs",
                                StringComparison.Ordinal));
                    });
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)),
                "Diagnostic log automatic refresh UI test timed out.");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }

        Assert.Null(threadException);
    }

    /// <summary>Keeps the UI context installed throughout asynchronous browsing and closes on the owning thread.</summary>
    private static void RunWithMessageLoop(Form form, Func<Task> scenario)
    {
        Exception? failure = null;
        form.Shown += async (_, _) =>
        {
            try
            {
                Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
                await Task.Yield();
                await scenario();
                Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                form.Close();
            }
        };

        // A standalone DoEvents loop uninstalls the WinForms context when it exits,
        // letting await continuations race control creation/disposal on a pool thread.
        Application.Run(form);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Creates an invisible editor with deterministic service discovery.</summary>
    private static ConfigurationEditorForm CreateForm(string configPath) => new(
        configPath,
        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
        notificationPublisher: null
    )
    {
        ShowInTaskbar = false,
        Opacity = 0
    };

    /// <summary>Yields to the real WinForms loop until a bounded asynchronous UI condition is met.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? describeState = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), $"The asynchronous diagnostic-log UI condition was not met. {describeState?.Invoke()}");
    }

    /// <summary>Finds one named control recursively beneath a tab page.</summary>
    private static T FindNamedControl<T>(Control root, string name) where T : Control
    {
        return Assert.Single(
            EnumerateControls(root).OfType<T>(),
            control => control.Name == name
        );
    }

    /// <summary>Recursively enumerates one control tree.</summary>
    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
        {
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
        }
    }

    /// <summary>Creates one isolated directory for diagnostic log editor tests.</summary>
    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.DiagnosticLogEditorTests-{Guid.NewGuid():N}"
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
                "AppSupervisor.DiagnosticLogEditorTests-",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Temporary diagnostic-log UI test path validation failed.");
        }

        Directory.Delete(fullPath, recursive: true);
    }
}
