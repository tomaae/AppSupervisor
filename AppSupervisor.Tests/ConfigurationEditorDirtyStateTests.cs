using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies configuration editor change detection and action availability.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorDirtyStateTests
{
    /// <summary>Confirms change actions follow edits and become disabled again after an exact revert.</summary>
    [Fact]
    public void EditAndRevert_ProfileName_UpdatesChangeActions()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.DirtyEditorTests-{Guid.NewGuid():N}"
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
                    Name = "Original profile",
                    MonitorProcess = "notepad.exe",
                    Applications = [],
                    Services = []
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
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
                        notificationPublisher: null
                    );
                    form.CreateControl();
                    Control[] controls = EnumerateControls(form).ToArray();
                    Button validateButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Validate"
                    );
                    Button saveButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Save && Apply"
                    );
                    TextBox nameInput = Assert.Single(
                        controls.OfType<TextBox>(),
                        input => input.Text == "Original profile"
                    );

                    Assert.False(validateButton.Enabled);
                    Assert.False(saveButton.Enabled);

                    nameInput.Text = "Changed profile";

                    Assert.True(validateButton.Enabled);
                    Assert.True(saveButton.Enabled);

                    nameInput.Text = "Original profile";

                    Assert.False(validateButton.Enabled);
                    Assert.False(saveButton.Enabled);
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
                "Dirty-state editor test timed out."
            );
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }

    /// <summary>Confirms typed numeric text updates actions before focus commits NumericUpDown.Value.</summary>
    [Fact]
    public void EditAndRevert_TypedNumbers_UpdatesChangeActionsImmediately()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.NumericDirtyEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
        ConfigFileWriter.SaveAtomic(
            configPath,
            new AppSupervisorConfig
            {
                Profiles =
                [
                    new SupervisorProfileConfig
                    {
                        Name = "Numeric profile",
                        MonitorProcess = "notepad.exe",
                        CloseTimeoutSeconds = 20,
                        Applications =
                        [
                            new ManagedApplicationConfig
                            {
                                Path = Path.Combine(Environment.SystemDirectory, "notepad.exe")
                            }
                        ],
                        Services = [],
                        Delays =
                        [
                            new DelayResourceConfig
                            {
                                DurationMilliseconds = 100
                            }
                        ]
                    }
                ],
                Integrations = new IntegrationsConfig
                {
                    SteamVr = new SteamVrIntegrationConfig
                    {
                        ReminderIntervalMinutes = 5
                    }
                }
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
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
                        notificationPublisher: null
                    );
                    form.CreateControl();
                    Control[] controls = EnumerateControls(form).ToArray();
                    ListBox resourceList = Assert.Single(
                        controls.OfType<ListBox>(),
                        list => list.Items.Cast<object>().Any(item => item is DelayResourceConfig)
                    );
                    resourceList.SelectedItem = Assert.Single(
                        resourceList.Items.Cast<object>().OfType<DelayResourceConfig>()
                    );
                    Application.DoEvents();
                    controls = EnumerateControls(form).ToArray();
                    Button validateButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Validate"
                    );
                    Button saveButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Save && Apply"
                    );
                    NumericUpDown closeTimeout = Assert.Single(
                        controls.OfType<NumericUpDown>(),
                        numeric => numeric.Enabled && numeric.Value == 20
                    );
                    NumericUpDown startupWait = Assert.Single(
                        controls.OfType<NumericUpDown>(),
                        numeric => numeric.Value == 100 && numeric.Maximum > 100
                    );
                    NumericUpDown steamVrReminder = Assert.Single(
                        controls.OfType<NumericUpDown>(),
                        numeric => numeric.Value == 5
                    );

                    AssertTypedNumberDirtyTracking(
                        closeTimeout,
                        "20",
                        "21",
                        validateButton,
                        saveButton
                    );
                    AssertTypedNumberDirtyTracking(
                        startupWait,
                        "100",
                        "101",
                        validateButton,
                        saveButton
                    );
                    AssertTypedNumberDirtyTracking(
                        steamVrReminder,
                        "5",
                        "6",
                        validateButton,
                        saveButton
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
                "Numeric dirty-state editor test timed out."
            );
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }

    /// <summary>Types and reverts one number while asserting immediate logical dirty-state changes.</summary>
    private static void AssertTypedNumberDirtyTracking(
        NumericUpDown numeric,
        string originalText,
        string changedText,
        Button validateButton,
        Button saveButton)
    {
        Assert.False(validateButton.Enabled);
        Assert.False(saveButton.Enabled);

        numeric.Text = changedText;

        Assert.True(validateButton.Enabled);
        Assert.True(saveButton.Enabled);

        numeric.Text = originalText;

        Assert.False(validateButton.Enabled);
        Assert.False(saveButton.Enabled);
    }

    /// <summary>Recursively enumerates a WinForms control hierarchy.</summary>
    /// <param name="root">The root control.</param>
    /// <returns>The root and all descendant controls.</returns>
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
