using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.HomeAssistant;
using AppSupervisor.ServiceControl;
using AppSupervisor.SteamVr;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies Home Assistant editor discovery, service filtering, and stateless-button options.</summary>
public sealed class ConfigurationEditorHomeAssistantTests
{
    [Fact]
    public void Constructor_HomeAssistantResource_FiltersEntitiesForSelectedService()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.HomeAssistantEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");
        ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
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
                    Name = "HA editor",
                    MonitorProcess = "notepad.exe",
                    HomeAssistantResources =
                    [
                        new HomeAssistantResourceConfig
                        {
                            Service = "switch.turn_on",
                            EntityId = "switch.power",
                            EntityName = "Power"
                        }
                    ]
                }
            ]
        });
        var catalog = new HomeAssistantCatalog(
            "2026.7.3",
            [
                new HomeAssistantServiceInfo("button.press", ["button"]),
                new HomeAssistantServiceInfo("switch.turn_on", ["switch"]),
                new HomeAssistantServiceInfo("switch.turn_off", ["switch"])
            ],
            [
                new HomeAssistantEntityInfo("switch.power", "Power", "off"),
                new HomeAssistantEntityInfo("button.restart", "Restart", "unknown")
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
                        path,
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
                        notificationPublisher: null,
                        steamVrDeviceLoader: _ => Task.FromResult(
                            new SteamVrSnapshot(false, null, [])
                        ),
                        homeAssistantCatalogLoader: (_, _) => Task.FromResult(catalog)
                    );
                    form.CreateControl();
                    Application.DoEvents();
                    Control[] controls = EnumerateControls(form).ToArray();
                    Button add = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Add..."
                    );
                    Assert.DoesNotContain(
                        controls.OfType<Button>(),
                        button => button.Text is "Add application" or "Add service"
                    );
                    Assert.NotNull(add);
                    Label actionLabel = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Text == "Action"
                    );
                    Assert.Single(
                        actionLabel.Parent!.Controls.OfType<Button>(),
                        button => button.Text == "Test action"
                    );
                    ComboBox service = Assert.Single(
                        controls.OfType<ComboBox>(),
                        combo => combo.Items.Cast<object>().OfType<HomeAssistantServiceInfo>().Any()
                    );
                    ComboBox entity = Assert.Single(
                        controls.OfType<ComboBox>(),
                        combo => combo.Items.Cast<object>().OfType<HomeAssistantEntityInfo>().Any()
                    );
                    Assert.Equal(
                        ["switch.power"],
                        entity.Items.Cast<HomeAssistantEntityInfo>().Select(item => item.EntityId)
                    );

                    service.SelectedItem = service.Items.Cast<HomeAssistantServiceInfo>()
                        .Single(item => item.Service == "button.press");
                    Application.DoEvents();

                    Assert.Equal(
                        ["button.restart"],
                        entity.Items.Cast<HomeAssistantEntityInfo>().Select(item => item.EntityId)
                    );
                    CheckBox verify = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Text == "Verify requested state change"
                    );
                    CheckBox persistent = Assert.Single(
                        controls.OfType<CheckBox>(),
                        checkBox => checkBox.Text.StartsWith("Keep this state persistent")
                    );
                    Assert.False(verify.Enabled);
                    Assert.False(persistent.Enabled);
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "HA editor test timed out.");
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
