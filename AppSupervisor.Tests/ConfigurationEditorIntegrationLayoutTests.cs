using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Drawing;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies that global integration settings use the editor's shared label and input grid.</summary>
public sealed class ConfigurationEditorIntegrationLayoutTests
{
    /// <summary>Confirms compact editor windows can scroll to integrations below the fold.</summary>
    [Fact]
    public void Constructor_CompactIntegrationsPage_ShowsVerticalScrollbar()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.IntegrationScrollTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig());
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new ConfigurationEditorForm(
                        configPath,
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
                        notificationPublisher: null
                    )
                    {
                        ShowInTaskbar = false,
                        Opacity = 0,
                        Size = new Size(1180, 700)
                    };
                    form.Show();
                    Application.DoEvents();
                    TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                    tabs.SelectedTab = Assert.Single(
                        tabs.TabPages.Cast<TabPage>(),
                        page => page.Text == "Integrations"
                    );
                    Application.DoEvents();
                    form.PerformLayout();

                    Panel scrolling = Assert.Single(
                        EnumerateControls(tabs.SelectedTab)
                            .OfType<Panel>(),
                        panel => panel.Name == "IntegrationsScrollPanel"
                    );
                    Assert.True(scrolling.AutoScroll);
                    Assert.True(scrolling.VerticalScroll.Visible);
                    Assert.True(scrolling.DisplayRectangle.Height > scrolling.ClientSize.Height);

                    GroupBox twitch = Assert.Single(
                        EnumerateControls(scrolling).OfType<GroupBox>(),
                        group => group.Text == "Global — Twitch broadcaster"
                    );
                    scrolling.ScrollControlIntoView(twitch);
                    Application.DoEvents();

                    Assert.True(scrolling.VerticalScroll.Value > 0);

                    GroupBox steamVr = Assert.Single(
                        EnumerateControls(scrolling).OfType<GroupBox>(),
                        group => group.Text == "Global — SteamVR device monitoring"
                    );
                    scrolling.ScrollControlIntoView(steamVr);
                    Application.DoEvents();
                    form.PerformLayout();
                    DataGridView devices = Assert.Single(
                        EnumerateControls(steamVr).OfType<DataGridView>()
                    );

                    Assert.True(
                        devices.ClientSize.Height >= 200,
                        $"The SteamVR device table must retain at least 200 pixels of visible height; actual height was {devices.ClientSize.Height}."
                    );
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
                "Integration scrollbar layout test timed out."
            );
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }

    /// <summary>Confirms every visible SteamVR label and editor begins on its shared column edge.</summary>
    [Fact]
    public void Constructor_SteamVrSettings_AlignLabelsAndEditors()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.IntegrationLayoutTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig());
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new ConfigurationEditorForm(
                        configPath,
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
                        notificationPublisher: null
                    );
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();
                    TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                    tabs.SelectedTab = Assert.Single(
                        tabs.TabPages.Cast<TabPage>(),
                        page => page.Text == "Integrations"
                    );
                    Application.DoEvents();
                    form.PerformLayout();
                    Control[] controls = EnumerateControls(form).ToArray();

                    Label timingLabel = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible && label.Text == "Timing"
                    );
                    Label reminderLabel = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible && label.Text == "Reminder interval"
                    );
                    Label notificationsLabel = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible && label.Text == "Notifications"
                    );
                    Assert.Equal(ScreenLeft(timingLabel), ScreenLeft(reminderLabel));
                    Assert.Equal(ScreenLeft(timingLabel), ScreenLeft(notificationsLabel));

                    CheckBox enabled = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible &&
                            checkBox.Text == "Monitor expected SteamVR devices"
                    );
                    Label timingDescription = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Visible && label.Text.StartsWith("30-second startup grace")
                    );
                    NumericUpDown reminder = Assert.Single(
                        controls.OfType<NumericUpDown>(),
                        numeric => numeric.Visible && numeric.Maximum == 1_440
                    );
                    CheckBox popup = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Visible && checkBox.Text == "Popup dialog"
                    );
                    int labelLeft = OuterLeft(timingLabel);
                    int editorLeft = OuterLeft(timingDescription);
                    Assert.Equal(labelLeft, OuterLeft(enabled));
                    Assert.Equal(editorLeft, OuterLeft(reminder));
                    Assert.Equal(editorLeft, OuterLeft(popup));
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
                "SteamVR integration layout test timed out."
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

    /// <summary>Returns the absolute left edge including a control's non-client border.</summary>
    /// <param name="control">The visible control to locate.</param>
    /// <returns>The screen-relative outer left edge.</returns>
    private static int OuterLeft(Control control) =>
        control.Parent!.PointToScreen(control.Location).X;

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
