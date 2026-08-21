using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies the narrowly scoped warning shown for Automatic Windows services.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorAutomaticServiceWarningTests
{
    [Fact]
    public void ServiceSelection_WarnsOnlyWhenAutomaticServiceIsChosen()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.AutomaticServiceWarningTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig
        {
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "Service warning",
                    MonitorProcess = "notepad.exe"
                }
            ]
        });
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var warnedServices = new List<string>();
                    var automatic = new InstalledServiceInfo(
                        "AutomaticService",
                        "Automatic service",
                        null,
                        null,
                        isAutomaticStart: true
                    );
                    var manual = new InstalledServiceInfo(
                        "ManualService",
                        "Manual service",
                        null,
                        null
                    );
                    using var form = new ConfigurationEditorForm(
                        configPath,
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>(
                            [automatic, manual]
                        ),
                        notificationPublisher: null,
                        automaticServiceWarning: service =>
                            warnedServices.Add(service.ServiceName)
                    );
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();

                    Button addButton = Assert.Single(
                        EnumerateControls(form).OfType<Button>(),
                        button => button.Text == "Add..."
                    );
                    ContextMenuStrip menu = Assert.IsType<ContextMenuStrip>(
                        addButton.ContextMenuStrip
                    );
                    ToolStripItem addService = Assert.Single(
                        menu.Items.Cast<ToolStripItem>(),
                        item => item.Text == "Add service"
                    );
                    addService.PerformClick();
                    Application.DoEvents();

                    Assert.Equal(["AutomaticService"], warnedServices);

                    ComboBox serviceSelector = Assert.Single(
                        EnumerateControls(form).OfType<ComboBox>(),
                        combo => combo.Items.Count == 2 &&
                            combo.Items.Cast<object>().All(item => item is InstalledServiceInfo)
                    );
                    serviceSelector.SelectedItem = manual;
                    Application.DoEvents();
                    Assert.Equal(["AutomaticService"], warnedServices);

                    serviceSelector.SelectedItem = automatic;
                    Application.DoEvents();
                    Assert.Equal(
                        ["AutomaticService", "AutomaticService"],
                        warnedServices
                    );

                    CheckBox restart = Assert.Single(
                        EnumerateControls(form).OfType<CheckBox>(),
                        checkBox => checkBox.Text == "Restart after unexpected stop"
                    );
                    restart.Checked = !restart.Checked;
                    Application.DoEvents();
                    Assert.Equal(2, warnedServices.Count);
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
                "Automatic service warning test timed out."
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
