using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.Notifications;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies Startup macro controls preserve existing helper options and clear button labels.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class StartupMacroEditorTests
{
    [Fact]
    public void Constructor_MinimizeMacro_DisablesButPreservesMinimizeAfterStart()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"AppSupervisor.MacroEditor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");
        ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
        {
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "Macro editor",
                    MonitorProcess = "notepad.exe",
                    Applications =
                    [
                        new ManagedApplicationConfig
                        {
                            Path = Environment.ProcessPath!,
                            MinimizeAfterStart = true,
                            Notifications = new NotificationConfig { Target = [] },
                            StartupMacros =
                            [
                                new StartupMacroActionConfig
                                {
                                    Type = StartupMacroActionType.Minimize
                                }
                            ]
                        }
                    ]
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
                    using var form = new ConfigurationEditorForm(
                        path,
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
                        notificationPublisher: null
                    );
                    form.CreateControl();
                    Control[] controls = EnumerateControls(form).ToArray();
                    CheckBox minimize = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Text == "Minimize windows after starting"
                    );
                    ListBox macroList = Assert.Single(
                        controls.OfType<ListBox>(),
                        list => list.Items.Cast<object>().OfType<StartupMacroActionConfig>().Any()
                    );
                    Assert.Equal(DrawMode.OwnerDrawFixed, macroList.DrawMode);
                    Assert.Equal(
                        ConfigurationIconListRenderer.GetItemHeight(macroList),
                        macroList.ItemHeight
                    );
                    Button testAction = Assert.Single(
                        EnumerateControls(macroList.Parent!),
                        control => control is Button button && button.Text == "Test action"
                    ) as Button ?? throw new InvalidOperationException();

                    Assert.True(minimize.Checked);
                    Assert.False(minimize.Enabled);
                    Assert.True(testAction.Enabled);
                    Assert.True(controls.OfType<Button>().Count(button => button.Text == "Move up") >= 2);
                    Assert.True(controls.OfType<Button>().Count(button => button.Text == "Move down") >= 2);
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Startup macro editor test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;

        foreach (Control child in root.Controls)
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
    }
}
