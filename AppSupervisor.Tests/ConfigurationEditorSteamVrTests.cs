using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using AppSupervisor.SteamVr;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies SteamVR discovery updates existing editor rows as well as adding devices.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorSteamVrTests
{
    [Fact]
    public void DiscoverExistingDevice_UpdatedRoleRefreshesAssignmentCell()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.SteamVrEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig
        {
            Integrations = new IntegrationsConfig
            {
                SteamVr = new SteamVrIntegrationConfig
                {
                    Devices =
                    [
                        new SteamVrDeviceConfig
                        {
                            Enabled = true,
                            Name = "Waist tracker",
                            SerialNumber = "LHR-TEST",
                            ModelNumber = "Old model",
                            DeviceClass = SteamVrDeviceClass.GenericTracker,
                            Role = SteamVrDeviceRole.None
                        }
                    ]
                }
            }
        });
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
                        notificationPublisher: null,
                        steamVrDeviceLoader: _ => Task.FromResult(new SteamVrSnapshot(
                            true,
                            DateTime.UtcNow,
                            [
                                new SteamVrDeviceSnapshot(
                                    "LHR-TEST",
                                    "Updated model",
                                    SteamVrDeviceClass.GenericTracker,
                                    true,
                                    SteamVrDeviceRole.Waist
                                )
                            ]
                        ))
                    )
                    {
                        ShowInTaskbar = false,
                        Opacity = 0
                    };
                    form.Show();
                    Application.DoEvents();

                    TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                    tabs.SelectedTab = Assert.Single(
                        tabs.TabPages.Cast<TabPage>(),
                        page => page.Text == "Integrations"
                    );
                    Application.DoEvents();

                    DataGridView devices = Assert.Single(
                        EnumerateControls(tabs.SelectedTab).OfType<DataGridView>()
                    );
                    DataGridViewRow row = Assert.Single(
                        devices.Rows.Cast<DataGridViewRow>()
                    );
                    int assignmentColumn = Assert.Single(
                        devices.Columns.Cast<DataGridViewColumn>(),
                        column => column.HeaderText == "Assignment"
                    ).Index;
                    int modelColumn = Assert.Single(
                        devices.Columns.Cast<DataGridViewColumn>(),
                        column => column.HeaderText == "Model"
                    ).Index;
                    Assert.Equal("Unassigned", row.Cells[assignmentColumn].FormattedValue);

                    Button discover = Assert.Single(
                        EnumerateControls(tabs.SelectedTab).OfType<Button>(),
                        button => button.Text == "Discover from running SteamVR"
                    );
                    discover.PerformClick();
                    Application.DoEvents();

                    Assert.Equal("Waist", row.Cells[assignmentColumn].FormattedValue);
                    Assert.Equal("Updated model", row.Cells[modelColumn].FormattedValue);
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
                "SteamVR discovery editor test timed out."
            );
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }

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
