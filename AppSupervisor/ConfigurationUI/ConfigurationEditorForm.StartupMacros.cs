using AppSupervisor.Health;
using AppSupervisor.Configuration;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Adds ordered helper startup macro editing and one-shot diagnostics.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly ListBox _startupMacroList = new() { Dock = DockStyle.Fill, FormattingEnabled = true };
    private Button _testStartupActionButton = null!;
    private Button _testStartupMacroButton = null!;

    private Control BuildStartupMacroPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 230,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        buttons.Controls.Add(CreateButton("Add action", AddStartupActionClicked));
        buttons.Controls.Add(CreateButton("Edit", EditStartupActionClicked));
        buttons.Controls.Add(CreateButton("Remove", RemoveStartupActionClicked));
        buttons.Controls.Add(CreateButton("Move up", MoveStartupActionUpClicked));
        buttons.Controls.Add(CreateButton("Move down", MoveStartupActionDownClicked));
        _testStartupActionButton = CreateButton("Test action", TestStartupActionClicked);
        _testStartupMacroButton = CreateButton("Test macro", TestStartupMacroClicked);
        buttons.Controls.Add(_testStartupActionButton);
        buttons.Controls.Add(_testStartupMacroButton);
        panel.Controls.Add(_startupMacroList, 0, 0);
        panel.Controls.Add(buttons, 0, 1);

        _startupMacroList.Format += StartupMacroListFormat;
        _startupMacroList.DoubleClick += EditStartupActionClicked;
        _startupMacroList.SelectedIndexChanged += (_, _) => UpdateStartupMacroControls();
        return panel;
    }

    private void BindStartupMacroList(
        ManagedApplicationConfig? application,
        StartupMacroActionConfig? preferred = null)
    {
        _startupMacroList.Items.Clear();

        if (application is not null)
        {
            foreach (StartupMacroActionConfig action in application.StartupMacros)
                _startupMacroList.Items.Add(action);
        }

        if (preferred is not null && _startupMacroList.Items.Contains(preferred))
            _startupMacroList.SelectedItem = preferred;
        else if (_startupMacroList.Items.Count > 0)
            _startupMacroList.SelectedIndex = 0;

        UpdateStartupMacroControls();
    }

    private void UpdateStartupMacroControls()
    {
        bool hasApplication = SelectedApplication is not null;
        bool hasSelection = SelectedStartupMacroAction is not null;
        _testStartupActionButton.Enabled = hasSelection &&
            SelectedStartupMacroAction?.Type != StartupMacroActionType.Delay;
        _testStartupMacroButton.Enabled = hasApplication && _startupMacroList.Items.Count > 0;

        bool macroMinimizes = SelectedApplication?.StartupMacros.Any(action =>
            action.Type == StartupMacroActionType.Minimize) == true;
        _applicationMinimize.Enabled = hasApplication && !macroMinimizes;
    }

    private void StartupMacroListFormat(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is StartupMacroActionConfig action)
            e.Value = StartupMacroDisplay.Action(action);
    }

    private void AddStartupActionClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application)
            return;

        using var dialog = new StartupMacroActionEditorDialog(
            new StartupMacroActionConfig
            {
                Type = StartupMacroActionType.Delay,
                DelayMilliseconds = 2_000
            },
            JavaLauncherDetector.ResolveRuntimePath(application.Path)
        );

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
            return;

        application.StartupMacros.Add(dialog.Result);
        BindStartupMacroList(application, dialog.Result);
        UpdateStatus();
    }

    private void EditStartupActionClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application ||
            SelectedStartupMacroAction is not StartupMacroActionConfig selected)
        {
            return;
        }

        int index = application.StartupMacros.IndexOf(selected);
        using var dialog = new StartupMacroActionEditorDialog(
            ConfigJson.Clone(selected),
            JavaLauncherDetector.ResolveRuntimePath(application.Path)
        );

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null)
            return;

        application.StartupMacros[index] = dialog.Result;
        BindStartupMacroList(application, dialog.Result);
        UpdateStatus();
    }

    private void RemoveStartupActionClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application ||
            SelectedStartupMacroAction is not StartupMacroActionConfig selected)
        {
            return;
        }

        application.StartupMacros.Remove(selected);
        BindStartupMacroList(application);
        UpdateStatus();
    }

    private void MoveStartupActionUpClicked(object? sender, EventArgs e) => MoveStartupAction(-1);

    private void MoveStartupActionDownClicked(object? sender, EventArgs e) => MoveStartupAction(1);

    private void MoveStartupAction(int offset)
    {
        if (SelectedApplication is not ManagedApplicationConfig application ||
            SelectedStartupMacroAction is not StartupMacroActionConfig selected)
        {
            return;
        }

        int oldIndex = application.StartupMacros.IndexOf(selected);
        int newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= application.StartupMacros.Count)
            return;

        application.StartupMacros.RemoveAt(oldIndex);
        application.StartupMacros.Insert(newIndex, selected);
        BindStartupMacroList(application, selected);
        UpdateStatus();
    }

    private async void TestStartupActionClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application ||
            SelectedStartupMacroAction is not StartupMacroActionConfig action ||
            action.Type == StartupMacroActionType.Delay)
        {
            return;
        }

        await TestStartupActionsAsync(application, [ConfigJson.Clone(action)], "Startup action");
    }

    private async void TestStartupMacroClicked(object? sender, EventArgs e)
    {
        if (SelectedApplication is not ManagedApplicationConfig application)
            return;

        await TestStartupActionsAsync(
            application,
            application.StartupMacros.Select(ConfigJson.Clone).ToList(),
            "Startup macro"
        );
    }

    private async Task TestStartupActionsAsync(
        ManagedApplicationConfig application,
        IReadOnlyList<StartupMacroActionConfig> actions,
        string title)
    {
        _testStartupActionButton.Enabled = false;
        _testStartupMacroButton.Enabled = false;

        try
        {
            string runtimePath = JavaLauncherDetector.ResolveRuntimePath(application.Path);

            for (int index = 0; index < actions.Count; index++)
            {
                StartupMacroActionConfig action = actions[index];

                if (action.Type == StartupMacroActionType.Delay)
                {
                    await Task.Delay(action.DelayMilliseconds ?? 0);
                    continue;
                }

                IReadOnlySet<int> processIds = ProcessPathDiscovery.FindRunningProcessIds(
                    runtimePath,
                    useSharedCache: false
                );
                StartupMacroWindowActions.ExecutionResult result =
                    StartupMacroWindowActions.Execute(action, processIds);

                if (result.Status != StartupMacroWindowActions.ExecutionStatus.Succeeded)
                {
                    MessageBox.Show(
                        this,
                        $"Action {index + 1} ({StartupMacroDisplay.ActionType(action.Type)}) failed.\n\n{result.Detail}",
                        $"{title} failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                    return;
                }
            }

            MessageBox.Show(
                this,
                $"{title} completed successfully.",
                $"{title} test",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, $"{title} failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UpdateStartupMacroControls();
        }
    }

    private StartupMacroActionConfig? SelectedStartupMacroAction =>
        _startupMacroList.SelectedItem as StartupMacroActionConfig;
}

internal static class StartupMacroDisplay
{
    public static string Action(StartupMacroActionConfig action) => action.Type switch
    {
        StartupMacroActionType.Delay => $"Delay {action.DelayMilliseconds ?? 0} ms",
        StartupMacroActionType.Hotkey => $"Hotkey {StartupMacroWindowActions.FormatHotkey(action.Keys)}",
        StartupMacroActionType.MoveWindow => $"Move window to {Monitor(action.Monitor)} at {action.X}, {action.Y}",
        StartupMacroActionType.ResizeWindow => $"Resize window to {action.Width} x {action.Height}",
        StartupMacroActionType.Minimize => "Minimize window",
        StartupMacroActionType.Maximize => "Maximize window",
        StartupMacroActionType.Restore => "Restore window",
        StartupMacroActionType.BringToFront => "Bring window to front",
        _ => "Invalid startup macro action"
    };

    public static string ActionType(StartupMacroActionType? type) => type switch
    {
        StartupMacroActionType.Delay => "Delay",
        StartupMacroActionType.Hotkey => "Hotkey",
        StartupMacroActionType.MoveWindow => "Move window",
        StartupMacroActionType.ResizeWindow => "Resize window",
        StartupMacroActionType.Minimize => "Minimize",
        StartupMacroActionType.Maximize => "Maximize",
        StartupMacroActionType.Restore => "Restore",
        StartupMacroActionType.BringToFront => "Bring to front",
        _ => "Unknown"
    };

    private static string Monitor(string? monitor) => DisplayMonitorCatalog.Describe(monitor);
}
