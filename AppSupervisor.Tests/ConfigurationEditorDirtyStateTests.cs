using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies configuration editor change detection and action availability.</summary>
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
            [
                new SupervisorProfileConfig
                {
                    Name = "Original profile",
                    MonitorProcess = "notepad.exe",
                    Applications = [],
                    Services = []
                }
            ]
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
