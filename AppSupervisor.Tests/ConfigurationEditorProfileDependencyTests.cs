using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies profile prerequisite selection and persistence in the WinForms editor.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorProfileDependencyTests
{
    [Fact]
    public void DependencySelector_LoadsChoicesAndPersistsSelection()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.ProfileDependencyEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "config.json");
        var prerequisite = new SupervisorProfileConfig
        {
            ProfileId = "prerequisite",
            Name = "VR runtime",
            MonitorProcess = "vrserver.exe"
        };
        var dependent = new SupervisorProfileConfig
        {
            ProfileId = "dependent",
            Name = "Dependent app",
            DependencyProfileId = prerequisite.ProfileId,
            MonitorProcess = "dependent.exe"
        };
        ConfigFileWriter.SaveAtomic(
            configPath,
            new AppSupervisorConfig { Profiles = [dependent, prerequisite] }
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
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();

                    ComboBox dependency = EnumerateControls(form)
                        .OfType<ComboBox>()
                        .Single(comboBox => comboBox.Name == "ProfileDependencySelector");
                    Assert.Equal(2, dependency.Items.Count);
                    Assert.Equal("VR runtime", dependency.GetItemText(dependency.SelectedItem));

                    dependency.SelectedIndex = 0;
                    Application.DoEvents();
                    Button save = EnumerateControls(form).OfType<Button>()
                        .Single(button => button.Text == "Save && Apply");
                    save.PerformClick();
                    Application.DoEvents();
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
                "Profile dependency editor test timed out."
            );

            Assert.Null(threadException);
            SupervisorProfileConfig saved = ConfigLoader.Load(configPath).Profiles
                .Single(profile => profile.ProfileId == dependent.ProfileId);
            Assert.Equal("", saved.DependencyProfileId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;

            foreach (Control child in EnumerateControls(control))
                yield return child;
        }
    }
}
