using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Drawing;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies resource section separation and application/service option alignment.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorResourceLayoutTests
{
    /// <summary>Confirms the fixed divider and notification/input left edges after real WinForms layout.</summary>
    [Fact]
    public void Constructor_VisibleResourceLayout_AlignsOptionsAndShowsDivider()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.ResourceLayoutTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
        ConfigFileWriter.SaveAtomic(
            configPath,
            new AppSupervisorConfig
            {
                Profiles = [
                new SupervisorProfileConfig
                {
                    Name = "Resource layout",
                    MonitorProcess = "notepad.exe",
                    Applications =
                    [
                        new ManagedApplicationConfig
                        {
                            Path = Environment.ProcessPath!,
                            Arguments = "--layout-test",
                            Notifications = new NotificationConfig { Target = [] },
                            HealthChecks = []
                        }
                    ],
                    Services =
                    [
                        new ManagedServiceConfig
                        {
                            Enabled = false,
                            ServiceName = "LayoutService",
                            Notifications = new NotificationConfig { Target = [] }
                        }
                    ]
                }
            ]
            }
        );
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new ConfigurationEditorForm(
                        configPath,
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>(
                            [new InstalledServiceInfo("LayoutService", "Layout service", null, null)]
                        ),
                        notificationPublisher: null
                    );
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();
                    TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                    tabs.SelectedIndex = 1;
                    Application.DoEvents();
                    form.PerformLayout();
                    Control[] controls = EnumerateControls(form).ToArray();

                    Panel dividerContainer = Assert.Single(
                        controls.OfType<Panel>(),
                        panel => panel.Padding.Top == 1 &&
                            panel.BackColor == SystemColors.ControlDark
                    );
                    Assert.True(dividerContainer.Visible);
                    Assert.True(dividerContainer.Width > 100);

                    Button addResource = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Visible && button.Text == "Add..."
                    );
                    TableLayoutPanel resourceButtons = Assert.IsType<TableLayoutPanel>(
                        addResource.Parent
                    );
                    Button removeResource = Assert.Single(
                        resourceButtons.Controls.OfType<Button>(),
                        button => button.Text == "Remove"
                    );
                    Button moveUp = Assert.Single(
                        resourceButtons.Controls.OfType<Button>(),
                        button => button.Text == "Move up"
                    );
                    Assert.Equal(ScreenTop(addResource), ScreenTop(removeResource));
                    Assert.True(ScreenTop(moveUp) < ScreenTop(addResource));
                    Assert.InRange(
                        Math.Abs(addResource.Width - removeResource.Width),
                        0,
                        1
                    );

                    TextBox executable = Assert.Single(
                        controls.OfType<TextBox>(),
                        input => input.Text == Environment.ProcessPath
                    );
                    TextBox arguments = Assert.Single(
                        controls.OfType<TextBox>(),
                        input => input.Text == "--layout-test"
                    );
                    Assert.Equal(ScreenLeft(arguments), ScreenLeft(executable));

                    CheckBox applicationRestart = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible &&
                            checkBox.Text == "Restart after unexpected exit"
                    );
                    CheckBox applicationPopup = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible && checkBox.Text == "Popup dialog"
                    );
                    Assert.Equal(ScreenLeft(applicationRestart), ScreenLeft(applicationPopup));

                    ListBox resourceList = Assert.Single(
                        controls.OfType<ListBox>(),
                        list => list.Items.Count == 2 &&
                            list.Items.Cast<object>().All(item => item is ManagedResourceConfig)
                    );
                    Assert.Equal(DrawMode.OwnerDrawFixed, resourceList.DrawMode);
                    int standardIconSize = Math.Max(20, 20 * resourceList.DeviceDpi / 96);
                    Assert.Equal(
                        Math.Max(resourceList.Font.Height + 2, standardIconSize + 2),
                        resourceList.ItemHeight
                    );
                    ComboBox dependency = Assert.Single(
                        controls.OfType<ComboBox>(),
                        comboBox => comboBox.Name == "ResourceDependencySelector" &&
                            comboBox.Items.Count == 1 &&
                            comboBox.GetItemText(comboBox.Items[0]) == "(none)"
                    );
                    Assert.Equal(DrawMode.OwnerDrawFixed, dependency.DrawMode);
                    Assert.Equal(
                        ConfigurationIconListRenderer.GetItemHeight(dependency),
                        dependency.ItemHeight
                    );
                    CheckBox leaveRunning = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible &&
                            checkBox.Text == "Leave helper running after profile deactivates"
                    );
                    CheckBox ensureClosed = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible &&
                            checkBox.Text == "Ensure closed until needed"
                    );
                    Assert.False(leaveRunning.Checked);
                    Assert.True(ensureClosed.Enabled);

                    Label startupMacros = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible && label.Text == "Startup macros"
                    );
                    Label healthChecks = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible && label.Text == "Health checks"
                    );
                    TableLayoutPanel applicationLayout = Assert.IsType<TableLayoutPanel>(
                        startupMacros.Parent
                    );
                    Control startupPanel = applicationLayout.GetControlFromPosition(
                        1,
                        applicationLayout.GetRow(startupMacros)
                    )!;
                    Control healthPanel = applicationLayout.GetControlFromPosition(
                        1,
                        applicationLayout.GetRow(healthChecks)
                    )!;
                    ListBox startupList = Assert.Single(
                        EnumerateControls(startupPanel).OfType<ListBox>()
                    );
                    ListBox healthList = Assert.Single(
                        EnumerateControls(healthPanel).OfType<ListBox>()
                    );
                    Assert.Equal(DrawMode.OwnerDrawFixed, healthList.DrawMode);
                    Assert.Equal(
                        ConfigurationIconListRenderer.GetItemHeight(healthList),
                        healthList.ItemHeight
                    );
                    Assert.InRange(Math.Abs(ScreenTop(startupMacros) - ScreenTop(startupList)), 0, 8);
                    Assert.InRange(Math.Abs(ScreenTop(healthChecks) - ScreenTop(healthList)), 0, 8);

                    CheckBox responsiveness = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible &&
                            checkBox.Text == "Monitor application responsiveness"
                    );
                    Label responsivenessHelp = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible &&
                            label.Text.StartsWith("Responsiveness monitoring checks")
                    );
                    CheckBox minimizeAfterStart = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible &&
                            checkBox.Text == "Minimize windows after starting"
                    );
                    CheckBox forceKill = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible &&
                            checkBox.Text.StartsWith("Allow force-kill")
                    );
                    Label forceKillHelp = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible &&
                            label.Text.StartsWith("Force-kill is intentionally disabled")
                    );
                    Label notifications = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible && label.Text == "Notifications"
                    );
                    Assert.True(ScreenTop(responsiveness) < ScreenTop(responsivenessHelp));
                    Assert.True(ScreenTop(responsivenessHelp) < ScreenTop(minimizeAfterStart));
                    Assert.True(ScreenTop(forceKill) < ScreenTop(forceKillHelp));
                    Assert.True(ScreenTop(forceKillHelp) < ScreenTop(notifications));

                    leaveRunning.Checked = true;

                    Assert.False(ensureClosed.Checked);
                    Assert.False(ensureClosed.Enabled);
                    resourceList.SelectedIndex = 1;
                    Application.DoEvents();
                    form.PerformLayout();

                    Assert.Equal(2, dependency.Items.Count);
                    string resourceChoice = Assert.IsType<string>(
                        dependency.GetItemText(dependency.Items[1])
                    );
                    Assert.DoesNotContain('[', resourceChoice);
                    Assert.DoesNotContain("Application", resourceChoice);

                    CheckBox serviceRestart = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible &&
                            checkBox.Text == "Restart after unexpected stop"
                    );
                    CheckBox servicePopup = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible && checkBox.Text == "Popup dialog"
                    );
                    Assert.Equal(ScreenLeft(serviceRestart), ScreenLeft(servicePopup));
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(
                thread.Join(TimeSpan.FromSeconds(10)),
                "Resource layout test timed out."
            );
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }

    /// <summary>Returns a control's absolute horizontal position after WinForms layout.</summary>
    /// <param name="control">The visible control to locate.</param>
    /// <returns>The screen-relative left edge.</returns>
    private static int ScreenLeft(Control control) =>
        control.PointToScreen(Point.Empty).X;

    /// <summary>Returns a control's absolute vertical position after WinForms layout.</summary>
    /// <param name="control">The visible control to locate.</param>
    /// <returns>The screen-relative top edge.</returns>
    private static int ScreenTop(Control control) =>
        control.PointToScreen(Point.Empty).Y;

    /// <summary>Recursively enumerates one control and every descendant.</summary>
    /// <param name="root">The root control.</param>
    /// <returns>The complete control hierarchy.</returns>
    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;

        foreach (Control child in root.Controls)
        {
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
        }
    }
}
