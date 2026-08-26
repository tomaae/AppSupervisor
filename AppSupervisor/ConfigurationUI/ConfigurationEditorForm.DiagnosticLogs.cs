namespace AppSupervisor.ConfigurationUI;

/// <summary>Provides the configuration editor's parsed, asynchronously refreshed diagnostic log viewer.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly TabPage _diagnosticLogsPage = new("Diagnostic logs");
    private readonly ComboBox _diagnosticLogSessionSelector = new()
    {
        Name = "DiagnosticLogSessionSelector",
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        FormattingEnabled = true
    };
    private readonly Button _refreshDiagnosticLogsButton = new()
    {
        Name = "RefreshDiagnosticLogsButton",
        Text = "Refresh",
        AutoSize = true,
        Margin = Padding.Empty
    };
    private readonly Label _diagnosticLogStatus = new()
    {
        Name = "DiagnosticLogStatus",
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = SystemColors.GrayText
    };
    private readonly DataGridView _diagnosticLogRecords = new()
    {
        Name = "DiagnosticLogRecords",
        Dock = DockStyle.Fill,
        ReadOnly = true,
        VirtualMode = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.Fixed3D,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
        MultiSelect = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private readonly RichTextBox _diagnosticLogDetail = new()
    {
        Name = "DiagnosticLogDetail",
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
        DetectUrls = false,
        WordWrap = false,
        ScrollBars = RichTextBoxScrollBars.Both
    };
    private readonly GroupBox _diagnosticLogDetailGroup = new()
    {
        Text = "Record detail",
        Dock = DockStyle.Fill,
        Padding = new Padding(8)
    };

    private IReadOnlyList<DiagnosticLogRecord> _displayedDiagnosticLogRecords = [];
    private CancellationTokenSource? _diagnosticLogOperationCancellation;
    private bool _updatingDiagnosticLogSessions;
    private bool _diagnosticLogsDisposed;

    /// <summary>Builds the session selector, structured record grid, and multiline detail pane.</summary>
    private TabPage BuildDiagnosticLogsPage()
    {
        _diagnosticLogsPage.Name = "DiagnosticLogsPage";
        _diagnosticLogsPage.Padding = new Padding(10);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = "Session:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 0)
        }, 0, 0);
        _diagnosticLogSessionSelector.Margin = new Padding(0, 0, 8, 0);
        header.Controls.Add(_diagnosticLogSessionSelector, 1, 0);
        header.Controls.Add(_refreshDiagnosticLogsButton, 2, 0);

        _diagnosticLogRecords.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Time",
            HeaderText = "Time",
            Width = 180,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _diagnosticLogRecords.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Level",
            HeaderText = "Level",
            Width = 82,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _diagnosticLogRecords.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Message",
            HeaderText = "Message",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 240,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _diagnosticLogDetail.Font = new Font(FontFamily.GenericMonospace, 9f);
        _diagnosticLogDetailGroup.Controls.Add(_diagnosticLogDetail);

        var content = new SplitContainer
        {
            Name = "DiagnosticLogSplitContainer",
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 360,
            Panel1MinSize = 150,
            Panel2MinSize = 100
        };
        content.Panel1.Controls.Add(_diagnosticLogRecords);
        content.Panel2.Controls.Add(_diagnosticLogDetailGroup);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(_diagnosticLogStatus, 0, 1);
        layout.Controls.Add(content, 0, 2);
        _diagnosticLogsPage.Controls.Add(layout);
        return _diagnosticLogsPage;
    }

    /// <summary>Connects refresh triggers after the tab's controls have been constructed.</summary>
    private void InitializeDiagnosticLogs()
    {
        _tabs.SelectedIndexChanged += DiagnosticLogTabSelectionChanged;
        _diagnosticLogSessionSelector.SelectedIndexChanged +=
            DiagnosticLogSessionSelectionChanged;
        _refreshDiagnosticLogsButton.Click += RefreshDiagnosticLogsClicked;
        _diagnosticLogRecords.CellValueNeeded += DiagnosticLogCellValueNeeded;
        _diagnosticLogRecords.CellFormatting += DiagnosticLogCellFormatting;
        _diagnosticLogRecords.SelectionChanged += DiagnosticLogRecordSelectionChanged;
        _diagnosticLogStatus.Text = "Select this tab to load available session logs.";
    }

    /// <summary>Reloads session discovery and content each time the user returns to this tab.</summary>
    private void DiagnosticLogTabSelectionChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_tabs.SelectedTab, _diagnosticLogsPage))
            BeginRefreshDiagnosticLogs();
    }

    /// <summary>Starts an explicit session rediscovery and content reload.</summary>
    private void RefreshDiagnosticLogsClicked(object? sender, EventArgs e) =>
        BeginRefreshDiagnosticLogs();

    /// <summary>Loads a newly selected session without repeating directory discovery.</summary>
    private void DiagnosticLogSessionSelectionChanged(object? sender, EventArgs e)
    {
        if (_updatingDiagnosticLogSessions ||
            _diagnosticLogSessionSelector.SelectedItem is not DiagnosticLogSession session)
        {
            return;
        }

        BeginLoadDiagnosticLogSession(session);
    }

    /// <summary>Cancels stale work and starts one full refresh without blocking the UI thread.</summary>
    private void BeginRefreshDiagnosticLogs()
    {
        if (_diagnosticLogsDisposed || IsDisposed || Disposing)
            return;

        CancellationTokenSource cancellation = ReplaceDiagnosticLogOperation();
        _ = RunDiagnosticLogOperationAsync(cancellation, RefreshDiagnosticLogsAsync);
    }

    /// <summary>Cancels stale work and reads one selected log without blocking the UI thread.</summary>
    private void BeginLoadDiagnosticLogSession(DiagnosticLogSession session)
    {
        if (_diagnosticLogsDisposed || IsDisposed || Disposing)
            return;

        CancellationTokenSource cancellation = ReplaceDiagnosticLogOperation();
        _ = RunDiagnosticLogOperationAsync(
            cancellation,
            token => LoadDiagnosticLogSessionAsync(session, token)
        );
    }

    /// <summary>Allocates the sole current viewer operation after cancelling its predecessor.</summary>
    private CancellationTokenSource ReplaceDiagnosticLogOperation()
    {
        CancellationTokenSource? previous = _diagnosticLogOperationCancellation;
        var current = new CancellationTokenSource();
        _diagnosticLogOperationCancellation = current;
        previous?.Cancel();
        return current;
    }

    /// <summary>Owns one cancellation source through asynchronous completion before disposing it.</summary>
    private async Task RunDiagnosticLogOperationAsync(
        CancellationTokenSource cancellation,
        Func<CancellationToken, Task> operation)
    {
        try
        {
            await operation(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_diagnosticLogOperationCancellation, cancellation))
                _diagnosticLogOperationCancellation = null;
            cancellation.Dispose();
        }
    }

    /// <summary>Discovers available logs in the background, preserves selection, and loads the chosen session.</summary>
    private async Task RefreshDiagnosticLogsAsync(CancellationToken cancellationToken)
    {
        string? preferredPath =
            (_diagnosticLogSessionSelector.SelectedItem as DiagnosticLogSession)?.Path;
        SetDiagnosticLogBusy("Finding session logs...");

        try
        {
            string directory = Path.GetDirectoryName(_configPath)!;
            DiagnosticLogDiscoveryResult discovery = await Task.Run(
                () => DiagnosticLogReader.DiscoverSessions(directory),
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();

            if (_diagnosticLogsDisposed || IsDisposed || Disposing)
                return;

            _updatingDiagnosticLogSessions = true;
            DiagnosticLogSession? selected;
            try
            {
                _diagnosticLogSessionSelector.Items.Clear();
                foreach (DiagnosticLogSession session in discovery.Sessions)
                    _diagnosticLogSessionSelector.Items.Add(session);

                selected = discovery.Sessions.FirstOrDefault(session =>
                    string.Equals(session.Path, preferredPath, StringComparison.OrdinalIgnoreCase)
                ) ?? discovery.Sessions.FirstOrDefault();
                _diagnosticLogSessionSelector.SelectedItem = selected;
            }
            finally
            {
                _updatingDiagnosticLogSessions = false;
            }

            if (selected is null)
            {
                ShowDiagnosticLogRecords([]);
                _diagnosticLogStatus.Text = discovery.Warning ??
                    "No AppSupervisor session logs are currently available.";
                return;
            }

            await LoadDiagnosticLogSessionAsync(selected, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_diagnosticLogsDisposed && !IsDisposed && !Disposing)
            {
                ShowDiagnosticLogRecords([]);
                _diagnosticLogStatus.Text = $"Session logs could not be refreshed: {exception.Message}";
            }
        }
        finally
        {
            ClearDiagnosticLogBusy(cancellationToken);
        }
    }

    /// <summary>Reads one selected file with writer/delete sharing and replaces the virtual record set atomically.</summary>
    private async Task LoadDiagnosticLogSessionAsync(
        DiagnosticLogSession session,
        CancellationToken cancellationToken)
    {
        SetDiagnosticLogBusy($"Loading {session.FileName}...");

        try
        {
            DiagnosticLogReadResult result = await DiagnosticLogReader.ReadAsync(
                session.Path,
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();

            if (_diagnosticLogsDisposed || IsDisposed || Disposing)
                return;

            ShowDiagnosticLogRecords(result.Records);
            int malformedCount = result.Records.Count(record => record.IsMalformed);
            var notes = new List<string>
            {
                $"{result.Records.Count:N0} record(s)",
                session.FileName
            };
            if (malformedCount > 0)
                notes.Add($"{malformedCount:N0} malformed");
            if (result.WasByteLimited)
                notes.Add("showing the newest 16 MB");
            if (result.OmittedRecordCount > 0)
                notes.Add($"{result.OmittedRecordCount:N0} older parsed record(s) omitted");
            _diagnosticLogStatus.Text = string.Join(" — ", notes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (FileNotFoundException)
        {
            ShowDiagnosticLogFileUnavailable(session, "The selected log rotated or was removed.");
        }
        catch (DirectoryNotFoundException)
        {
            ShowDiagnosticLogFileUnavailable(session, "The log directory was removed.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowDiagnosticLogFileUnavailable(
                session,
                $"The selected log could not be read: {exception.Message}"
            );
        }
        catch (Exception exception)
        {
            ShowDiagnosticLogFileUnavailable(
                session,
                $"The selected log could not be parsed: {exception.Message}"
            );
        }
        finally
        {
            ClearDiagnosticLogBusy(cancellationToken);
        }
    }

    /// <summary>Publishes a contained missing/rotating/inaccessible-file state.</summary>
    private void ShowDiagnosticLogFileUnavailable(DiagnosticLogSession session, string message)
    {
        if (_diagnosticLogsDisposed || IsDisposed || Disposing)
            return;

        ShowDiagnosticLogRecords([]);
        _diagnosticLogStatus.Text = $"{message} Refresh to rediscover sessions. ({session.FileName})";
    }

    /// <summary>Sets the virtual grid's immutable row source and selects the newest record.</summary>
    private void ShowDiagnosticLogRecords(IReadOnlyList<DiagnosticLogRecord> records)
    {
        _diagnosticLogRecords.CurrentCell = null;
        _diagnosticLogRecords.RowCount = 0;
        _displayedDiagnosticLogRecords = records;
        _diagnosticLogRecords.RowCount = records.Count;
        _diagnosticLogDetail.Clear();
        _diagnosticLogDetailGroup.Text = "Record detail";

        if (records.Count == 0)
            return;

        int newestIndex = records.Count - 1;
        _diagnosticLogRecords.CurrentCell = _diagnosticLogRecords.Rows[newestIndex].Cells[0];
        _diagnosticLogRecords.FirstDisplayedScrollingRowIndex = newestIndex;
        ShowDiagnosticLogRecord(newestIndex);
    }

    /// <summary>Supplies values only for virtual rows that WinForms needs to paint.</summary>
    private void DiagnosticLogCellValueNeeded(
        object? sender,
        DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _displayedDiagnosticLogRecords.Count)
            return;

        DiagnosticLogRecord record = _displayedDiagnosticLogRecords[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            0 => record.TimeText,
            1 => record.Level,
            2 => record.DisplayMessage,
            _ => null
        };
    }

    /// <summary>Visually distinguishes severity while leaving record text and selection accessible.</summary>
    private void DiagnosticLogCellFormatting(
        object? sender,
        DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex != 1 || e.RowIndex < 0 ||
            e.RowIndex >= _displayedDiagnosticLogRecords.Count)
        {
            return;
        }

        DiagnosticLogRecord record = _displayedDiagnosticLogRecords[e.RowIndex];
        e.CellStyle.ForeColor = record.Level switch
        {
            "ERROR" => Color.DarkRed,
            "WARN" => Color.DarkGoldenrod,
            "TRACE" => SystemColors.GrayText,
            _ when record.IsMalformed => Color.DarkMagenta,
            _ => SystemColors.ControlText
        };
    }

    /// <summary>Displays the selected record's complete multiline body without flattening continuations.</summary>
    private void DiagnosticLogRecordSelectionChanged(object? sender, EventArgs e) =>
        ShowSelectedDiagnosticLogRecord();

    private void ShowSelectedDiagnosticLogRecord()
    {
        int rowIndex = _diagnosticLogRecords.CurrentCell?.RowIndex ?? -1;
        ShowDiagnosticLogRecord(rowIndex);
    }

    /// <summary>Displays one record by row index even before WinForms assigns a current cell.</summary>
    private void ShowDiagnosticLogRecord(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _displayedDiagnosticLogRecords.Count)
            return;

        DiagnosticLogRecord record = _displayedDiagnosticLogRecords[rowIndex];
        _diagnosticLogDetailGroup.Text =
            $"Record detail — {record.TimeText} [{record.Level}]";
        _diagnosticLogDetail.Text = record.FullText;
        _diagnosticLogDetail.SelectionStart = 0;
        _diagnosticLogDetail.SelectionLength = 0;
        _diagnosticLogDetail.ScrollToCaret();
    }

    /// <summary>Disables the explicit refresh command during one current asynchronous operation.</summary>
    private void SetDiagnosticLogBusy(string status)
    {
        if (_diagnosticLogsDisposed || IsDisposed || Disposing)
            return;

        _refreshDiagnosticLogsButton.Enabled = false;
        _refreshDiagnosticLogsButton.Text = "Refreshing...";
        _diagnosticLogStatus.Text = status;
    }

    /// <summary>Restores the refresh command only when the completing operation is still current.</summary>
    private void ClearDiagnosticLogBusy(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _diagnosticLogsDisposed ||
            IsDisposed || Disposing)
        {
            return;
        }

        _refreshDiagnosticLogsButton.Enabled = true;
        _refreshDiagnosticLogsButton.Text = "Refresh";
    }

    /// <summary>Cancels outstanding file work and detaches viewer events during form disposal.</summary>
    private void DisposeDiagnosticLogs()
    {
        if (_diagnosticLogsDisposed)
            return;

        _diagnosticLogsDisposed = true;
        _tabs.SelectedIndexChanged -= DiagnosticLogTabSelectionChanged;
        _diagnosticLogSessionSelector.SelectedIndexChanged -=
            DiagnosticLogSessionSelectionChanged;
        _refreshDiagnosticLogsButton.Click -= RefreshDiagnosticLogsClicked;
        _diagnosticLogRecords.CellValueNeeded -= DiagnosticLogCellValueNeeded;
        _diagnosticLogRecords.CellFormatting -= DiagnosticLogCellFormatting;
        _diagnosticLogRecords.SelectionChanged -= DiagnosticLogRecordSelectionChanged;
        _diagnosticLogOperationCancellation?.Cancel();
        _diagnosticLogOperationCancellation = null;
    }
}
