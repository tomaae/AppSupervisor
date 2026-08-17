using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.HomeAssistant;
using AppSupervisor.Obs;
using AppSupervisor.ServiceControl;
using AppSupervisor.SteamVr;
using AppSupervisor.WindowsAudio;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies every command exposed by the configuration editor's Add resource menu.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorAddResourceTests
{
    /// <summary>Confirms each menu choice creates and selects the corresponding resource type.</summary>
    [Fact]
    public void AddResourceMenu_AllChoices_AddTheirResources()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.AddResourceTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig
        {
            Integrations = new IntegrationsConfig
            {
                HomeAssistant = new HomeAssistantIntegrationConfig
                {
                    Url = "https://home-assistant.example:8123",
                    Token = "test-token"
                }
            },
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "Add resources",
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
                    using var form = new ConfigurationEditorForm(
                        configPath,
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>(
                            [new InstalledServiceInfo("TestService", "Test service", null, null)]
                        ),
                        notificationPublisher: null,
                        steamVrDeviceLoader: _ => Task.FromResult(
                            new SteamVrSnapshot(false, null, [])
                        ),
                        homeAssistantCatalogLoader: (_, _) => Task.FromResult(
                            new HomeAssistantCatalog(
                                "2026.8.0",
                                [new HomeAssistantServiceInfo("switch.turn_on", ["switch"])],
                                [new HomeAssistantEntityInfo("switch.test", "Test switch", "off")]
                            )
                        ),
                        obsCatalogLoader: (_, _) => Task.FromResult(
                            new ObsCatalog(
                                "5.6.0",
                                ["Main"],
                                ["Microphone"],
                                [new ObsSceneSource("Main", "Camera")]
                            )
                        ),
                        audioEndpointLoader: _ => Task.FromResult<IReadOnlyList<AudioEndpointSnapshot>>(
                            [
                            new AudioEndpointSnapshot(
                                "default-output-id",
                                "default-output-instance",
                                "3d8cb175-1c34-49ea-bf40-c831feb05221",
                                "Test speakers",
                                "Test audio",
                                AudioInterfaceDirection.Output,
                                FollowsDefault: true
                            ),
                            new AudioEndpointSnapshot(
                                "default-input-id",
                                "default-input-instance",
                                "349818ac-c51e-42f1-a81a-16baea0c1a4e",
                                "Test microphone",
                                "Test audio",
                                AudioInterfaceDirection.Input,
                                FollowsDefault: true
                            ),
                            new AudioEndpointSnapshot(
                                "test-endpoint",
                                "test-instance",
                                "54056bb1-4eb4-473b-8640-5f03e83f6871",
                                "Other speakers",
                                "Test audio",
                                AudioInterfaceDirection.Output
                            )]
                        )
                    );
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();

                    TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                    tabs.SelectedIndex = 1;
                    Application.DoEvents();

                    Button addButton = Assert.Single(
                        EnumerateControls(form).OfType<Button>(),
                        button => button.Text == "Add..."
                    );
                    ContextMenuStrip menu = Assert.IsType<ContextMenuStrip>(
                        addButton.ContextMenuStrip
                    );

                    InvokeMenuItem(menu, "Add application");
                    InvokeMenuItem(menu, "Add service");
                    InvokeMenuItem(menu, "Add delay");
                    InvokeMenuItem(menu, "Add Home Assistant");
                    InvokeMenuItem(menu, "Add OBS action");
                    InvokeMenuItem(menu, "Add Windows audio interface");
                    Application.DoEvents();

                    ListBox resources = Assert.Single(
                        EnumerateControls(form).OfType<ListBox>(),
                        list => list.Items.Count == 6 &&
                            list.Items.Cast<object>().All(item => item is ManagedResourceConfig)
                    );
                    Assert.Collection(
                        resources.Items.Cast<ManagedResourceConfig>(),
                        resource => Assert.IsType<ManagedApplicationConfig>(resource),
                        resource => Assert.IsType<ManagedServiceConfig>(resource),
                        resource => Assert.IsType<DelayResourceConfig>(resource),
                        resource => Assert.IsType<HomeAssistantResourceConfig>(resource),
                        resource => Assert.IsType<ObsResourceConfig>(resource),
                        resource => Assert.IsType<AudioInterfaceResourceConfig>(resource)
                    );
                    AudioInterfaceResourceConfig audio = Assert.IsType<AudioInterfaceResourceConfig>(
                        resources.Items[5]
                    );
                    Assert.True(audio.UseDefaultDevice);
                    Assert.Equal(AudioInterfaceDirection.Output, audio.Direction);
                    Assert.Single(
                        EnumerateControls(form).OfType<Button>(),
                        button => button.Visible && button.Text == "Test for 5 seconds"
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
                "Add resource menu test timed out."
            );
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }

    /// <summary>Invokes one named resource menu command and pumps its WinForms continuation.</summary>
    /// <param name="menu">The persistent Add resource menu.</param>
    /// <param name="text">The exact command label to invoke.</param>
    private static void InvokeMenuItem(ContextMenuStrip menu, string text)
    {
        ToolStripItem item = Assert.Single(
            menu.Items.Cast<ToolStripItem>(),
            candidate => candidate.Text == text
        );
        item.PerformClick();
        Application.DoEvents();
    }

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
