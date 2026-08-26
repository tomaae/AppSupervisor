using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies the MQTT broker and ordered publish editors expose safe controls.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorMqttTests
{
    [Fact]
    public void Constructor_LoadsMaskedBrokerCredentialsAndReversibleResource()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.MqttEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");
        ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
        {
            Integrations = new IntegrationsConfig
            {
                Mqtt = new MqttIntegrationConfig
                {
                    Host = "broker.example",
                    Port = 8883,
                    UseTls = true,
                    Username = "operator",
                    Password = "secret"
                }
            },
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "MQTT",
                    MonitorProcess = "mqtt-trigger.exe",
                    MqttResources =
                    [
                        new MqttResourceConfig
                        {
                            Topic = "device/set",
                            Payload = "ON",
                            VerificationTopic = "device/state",
                            DeactivationBehavior =
                                MqttDeactivationBehavior.PublishConfiguredPayload,
                            DeactivationTopic = "device/set",
                            DeactivationPayload = "OFF"
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
                    using var form = new ConfigurationEditorForm(path)
                    {
                        ShowInTaskbar = false,
                        Opacity = 0
                    };
                    form.Show();
                    Application.DoEvents();
                    Control[] controls = EnumerateControls(form).ToArray();

                    TextBox host = Assert.Single(
                        controls.OfType<TextBox>(),
                        control => control.Name == "MqttHostTextBox"
                    );
                    TextBox password = Assert.Single(
                        controls.OfType<TextBox>(),
                        control => control.Name == "MqttPasswordTextBox"
                    );
                    TextBox topic = Assert.Single(
                        controls.OfType<TextBox>(),
                        control => control.Name == "MqttTopicTextBox"
                    );
                    ComboBox behavior = Assert.Single(
                        controls.OfType<ComboBox>(),
                        control => control.Name == "MqttDeactivationBehaviorComboBox"
                    );

                    Assert.Equal("broker.example", host.Text);
                    Assert.Equal("secret", password.Text);
                    Assert.True(password.UseSystemPasswordChar);
                    Assert.Equal("device/set", topic.Text);
                    Assert.Equal(
                        MqttDeactivationBehavior.PublishConfiguredPayload,
                        behavior.SelectedItem
                    );
                    Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Name == "TestMqttConnectionButton"
                    );
                    Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Name == "TestMqttActionButton"
                    );

                    Button add = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Add..."
                    );
                    Assert.Contains(
                        add.ContextMenuStrip!.Items.Cast<ToolStripItem>(),
                        item => item.Text == "Add MQTT publish"
                    );
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "MQTT editor test timed out.");
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
