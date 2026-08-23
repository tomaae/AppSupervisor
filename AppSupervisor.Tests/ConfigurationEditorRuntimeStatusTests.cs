using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.Core;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies live cached-status repainting never rebuilds or reselects resource rows.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorRuntimeStatusTests
{
    [Fact]
    public void RefreshRuntimeStatusSnapshot_VisibleForm_PreservesSelectionAndListItems()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.RuntimeStatus-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");
        var application = new ManagedApplicationConfig
        {
            ResourceId = "application-id",
            Path = Environment.ProcessPath!
        };
        var service = new ManagedServiceConfig
        {
            ResourceId = "service-id",
            ServiceName = "StatusService"
        };
        var profile = new SupervisorProfileConfig
        {
            ProfileId = "profile-id",
            Name = "Runtime status",
            MonitorProcess = "notepad.exe",
            Applications = [application],
            Services = [service]
        };
        ConfigFileWriter.SaveAtomic(
            path,
            new AppSupervisorConfig { Profiles = [profile] }
        );
        ConfigurationRuntimeStatusSnapshot current =
            ConfigurationRuntimeStatusSnapshot.Empty;
        int readerCalls = 0;
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
                        notificationPublisher: null,
                        runtimeStatusReader: () =>
                        {
                            readerCalls++;
                            return current;
                        }
                    );
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.CreateControl();
                    Assert.False(form.RuntimeStatusRefreshActive);
                    Assert.Equal(0, readerCalls);

                    form.Show();
                    Application.DoEvents();
                    Assert.True(form.RuntimeStatusRefreshActive);
                    Assert.True(readerCalls >= 1);

                    ListBox resourceList = Assert.Single(
                        EnumerateControls(form).OfType<ListBox>(),
                        list => list.Items.Count == 2 &&
                            list.Items.Cast<object>().All(item => item is ManagedResourceConfig)
                    );
                    resourceList.SelectedIndex = 1;
                    object selected = resourceList.SelectedItem!;
                    object[] items = resourceList.Items.Cast<object>().ToArray();

                    current = new ConfigurationRuntimeStatusSnapshot(
                        new Dictionary<
                            ConfigurationResourceRuntimeStatusKey,
                            ConfigurationResourceRuntimeStatus>
                        {
                            [new(profile.ProfileId, application.ResourceId)] =
                                ConfigurationResourceRuntimeStatus.Starting,
                            [new(profile.ProfileId, service.ResourceId)] =
                                ConfigurationResourceRuntimeStatus.Running
                        }
                    );
                    form.RefreshRuntimeStatusSnapshot();

                    Assert.Same(selected, resourceList.SelectedItem);
                    Assert.Equal(1, resourceList.SelectedIndex);
                    Assert.Equal(items, resourceList.Items.Cast<object>());

                    form.Close();
                    Application.DoEvents();
                    Assert.False(form.RuntimeStatusRefreshActive);
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
                "Runtime status UI test timed out."
            );
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
        {
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
        }
    }
}
