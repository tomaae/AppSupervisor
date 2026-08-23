using AppSupervisor.ConfigurationUI;
using System.Drawing;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies monitor presentation and current-window readback controls in the macro action editor.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class StartupMacroActionEditorTests
{
    /// <summary>Confirms every action-specific editor begins at the Action selector's left edge.</summary>
    [Theory]
    [InlineData(StartupMacroActionType.Delay)]
    [InlineData(StartupMacroActionType.Hotkey)]
    [InlineData(StartupMacroActionType.MoveWindow)]
    [InlineData(StartupMacroActionType.ResizeWindow)]
    public void Constructor_ActionType_AlignsVisibleEditors(StartupMacroActionType type)
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new StartupMacroActionEditorDialog(
                    new StartupMacroActionConfig { Type = type },
                    Environment.ProcessPath!
                )
                {
                    ShowInTaskbar = false,
                    Opacity = 0
                };
                dialog.Show();
                Application.DoEvents();
                Control[] controls = EnumerateControls(dialog).ToArray();
                ComboBox action = Assert.Single(
                    controls.OfType<ComboBox>(),
                    combo => combo.Items.Cast<object>().OfType<StartupMacroActionType>().Any()
                );
                Assert.Equal(DrawMode.OwnerDrawFixed, action.DrawMode);
                Assert.Equal(
                    ConfigurationIconListRenderer.GetItemHeight(action),
                    action.ItemHeight
                );
                IEnumerable<Control> visibleEditors = controls.Where(control =>
                    control.Visible && control != action &&
                    (control is NumericUpDown ||
                        control is HotkeyCaptureTextBox ||
                        control is ComboBox combo &&
                            combo.Items.Cast<object>()
                                .OfType<DisplayMonitorCatalog.MonitorChoice>().Any() ||
                        control is Button button &&
                            button.Text.StartsWith("Read current", StringComparison.Ordinal))
                );

                foreach (Control editor in visibleEditors)
                    Assert.Equal(OuterLeft(action), OuterLeft(editor));
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Macro editor alignment test timed out.");
        Assert.Null(threadException);
    }

    [Fact]
    public void Constructor_MoveAction_ShowsNamedMonitorsAndPositionReadback()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new StartupMacroActionEditorDialog(
                    new StartupMacroActionConfig
                    {
                        Type = StartupMacroActionType.MoveWindow,
                        X = 0,
                        Y = 0
                    },
                    Environment.ProcessPath!
                );
                dialog.ShowInTaskbar = false;
                dialog.Opacity = 0;
                dialog.Show();
                Application.DoEvents();
                Control[] controls = EnumerateControls(dialog).ToArray();
                ComboBox monitor = Assert.Single(
                    controls.OfType<ComboBox>(),
                    combo => combo.Items.Cast<object>()
                        .OfType<DisplayMonitorCatalog.MonitorChoice>()
                        .Any()
                );
                Button readPosition = Assert.Single(
                    controls.OfType<Button>(),
                    button => button.Text == "Read current position"
                );
                Button readSize = Assert.Single(
                    controls.OfType<Button>(),
                    button => button.Text == "Read current size"
                );

                Assert.True(readPosition.Visible);
                Assert.False(readSize.Visible);
                Assert.Equal(Screen.AllScreens.Length, monitor.Items.Count);

                foreach (Screen screen in Screen.AllScreens)
                {
                    string displayName = Assert.Single(
                        monitor.Items.Cast<DisplayMonitorCatalog.MonitorChoice>(),
                        choice => choice.DeviceName == screen.DeviceName
                    ).DisplayName;
                    Assert.Contains(screen.DeviceName, displayName);

                    string? friendlyName = DisplayMonitorCatalog.GetFriendlyName(screen.DeviceName);
                    if (!string.IsNullOrWhiteSpace(friendlyName))
                        Assert.Contains(friendlyName, displayName);

                    string actionText = StartupMacroDisplay.Action(new StartupMacroActionConfig
                    {
                        Type = StartupMacroActionType.MoveWindow,
                        Monitor = screen.DeviceName,
                        X = 26,
                        Y = 26
                    });
                    Assert.Equal($"Move window to {displayName} at 26, 26", actionText);
                }
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Macro action editor test timed out.");
        Assert.Null(threadException);
    }

    [Fact]
    public void ToMonitorRelativePosition_OffsetsFromSelectedWorkingArea()
    {
        Point relative = StartupMacroWindowActions.ToMonitorRelativePosition(
            new Rectangle(-1_800, 125, 800, 600),
            new Rectangle(-1_920, 40, 1_920, 1_040)
        );

        Assert.Equal(new Point(120, 85), relative);
    }

    /// <summary>Uses the picker's friendly display identity when formatting a configured monitor.</summary>
    [Fact]
    public void Describe_KnownMonitor_ReturnsPickerDisplayName()
    {
        DisplayMonitorCatalog.MonitorChoice[] monitors =
        [
            new(
                @"\\.\DISPLAY1",
                @"Dell UltraSharp (\\.\DISPLAY1) — Primary",
                new Rectangle(0, 0, 1920, 1040),
                Primary: true
            ),
            new(
                @"\\.\DISPLAY2",
                @"LG UltraGear (\\.\DISPLAY2)",
                new Rectangle(1920, 0, 2560, 1400),
                Primary: false
            )
        ];

        string description = DisplayMonitorCatalog.Describe(@"\\.\DISPLAY2", monitors);

        Assert.Equal(@"LG UltraGear (\\.\DISPLAY2)", description);
    }

    /// <summary>Identifies a configured monitor that is no longer connected.</summary>
    [Fact]
    public void Describe_DisconnectedMonitor_ReturnsExplicitFallback()
    {
        string description = DisplayMonitorCatalog.Describe(
            @"\\.\DISPLAY3",
            Array.Empty<DisplayMonitorCatalog.MonitorChoice>()
        );

        Assert.Equal(@"\\.\DISPLAY3 (disconnected)", description);
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;

        foreach (Control child in root.Controls)
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
    }

    private static int OuterLeft(Control control) =>
        control.Parent!.PointToScreen(control.Location).X;
}
