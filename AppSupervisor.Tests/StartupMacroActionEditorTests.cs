using AppSupervisor.ConfigurationUI;
using System.Drawing;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies monitor presentation and current-window readback controls in the macro action editor.</summary>
public sealed class StartupMacroActionEditorTests
{
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

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;

        foreach (Control child in root.Controls)
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
    }
}
