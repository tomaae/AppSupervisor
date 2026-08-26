using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Reflection;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies configuration-editor profile transfer actions and safe UI outcomes.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorProfileTransferTests
{
    [Fact]
    public void ExportThenImport_ProfileActionsUseDialogsAndCreateDisabledDirtyProfile()
    {
        string directory = CreateTemporaryDirectory();
        string configPath = Path.Combine(directory, "config.json");
        string exportPath = Path.Combine(directory, "shared.appsupervisor-profile.json");
        ConfigFileWriter.SaveAtomic(configPath, CreateConfiguration());
        var interaction = new FakeProfileTransferInteraction { ExportPath = exportPath };
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = CreateForm(configPath, interaction);
                    form.CreateControl();
                    Control[] controls = EnumerateControls(form).ToArray();
                    Button exportButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Export profile..."
                    );
                    Button importButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Import profile..."
                    );
                    Button validateButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Validate"
                    );
                    ComboBox selector = Assert.Single(
                        controls.OfType<ComboBox>(),
                        combo => combo.Items.Cast<object>().Any(item => item is SupervisorProfileConfig)
                    );

                    Assert.True(exportButton.Enabled);
                    Assert.False(validateButton.Enabled);
                    Click(exportButton);

                    Assert.True(File.Exists(exportPath));
                    string json = File.ReadAllText(exportPath);
                    Assert.DoesNotContain("home-assistant-secret", json);
                    Assert.DoesNotContain("obs-secret", json);
                    Assert.DoesNotContain("integrations", json, StringComparison.OrdinalIgnoreCase);
                    Assert.False(validateButton.Enabled);
                    Assert.Contains(interaction.Messages, message =>
                        message.Caption == "Profile exported" &&
                        message.Icon == MessageBoxIcon.Information);

                    interaction.ImportPath = exportPath;
                    Click(importButton);

                    Assert.Equal(2, selector.Items.Count);
                    SupervisorProfileConfig imported = Assert.IsType<SupervisorProfileConfig>(
                        selector.SelectedItem
                    );
                    Assert.Equal("Shared profile (Imported)", imported.Name);
                    Assert.False(imported.Enabled);
                    Assert.NotEqual("existing-profile-id", imported.ProfileId);
                    Assert.True(validateButton.Enabled);
                    Assert.Contains(interaction.Messages, message =>
                        message.Caption == "Profile imported" &&
                        message.Text.Contains("disabled profile", StringComparison.Ordinal) &&
                        message.Text.Contains("changed to", StringComparison.Ordinal));
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Profile transfer UI test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    [Fact]
    public void ImportMalformedProfile_ShowsErrorWithoutChangingEditor()
    {
        string directory = CreateTemporaryDirectory();
        string configPath = Path.Combine(directory, "config.json");
        string importPath = Path.Combine(directory, "malformed.json");
        ConfigFileWriter.SaveAtomic(configPath, CreateConfiguration());
        File.WriteAllText(importPath, "{ not valid json");
        var interaction = new FakeProfileTransferInteraction { ImportPath = importPath };
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = CreateForm(configPath, interaction);
                    form.CreateControl();
                    Control[] controls = EnumerateControls(form).ToArray();
                    Button importButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Import profile..."
                    );
                    Button validateButton = Assert.Single(
                        controls.OfType<Button>(),
                        button => button.Text == "Validate"
                    );
                    ComboBox selector = Assert.Single(
                        controls.OfType<ComboBox>(),
                        combo => combo.Items.Cast<object>().Any(item => item is SupervisorProfileConfig)
                    );

                    Click(importButton);

                    Assert.Single(selector.Items);
                    Assert.False(validateButton.Enabled);
                    ProfileTransferMessage error = Assert.Single(interaction.Messages);
                    Assert.Equal("Profile transfer error", error.Caption);
                    Assert.Equal(MessageBoxIcon.Error, error.Icon);
                    Assert.Contains("could not be imported", error.Text);
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Malformed import UI test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    [Fact]
    public void Constructor_NoProfiles_DisablesExportButKeepsImportAvailable()
    {
        string directory = CreateTemporaryDirectory();
        string configPath = Path.Combine(directory, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig());
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = CreateForm(configPath, new FakeProfileTransferInteraction());
                    form.CreateControl();
                    Button[] buttons = EnumerateControls(form).OfType<Button>().ToArray();

                    Assert.False(Assert.Single(
                        buttons,
                        button => button.Text == "Export profile..."
                    ).Enabled);
                    Assert.True(Assert.Single(
                        buttons,
                        button => button.Text == "Import profile..."
                    ).Enabled);
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Empty editor UI test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    private static ConfigurationEditorForm CreateForm(
        string configPath,
        IProfileTransferInteraction interaction)
    {
        return new ConfigurationEditorForm(
            configPath,
            _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
            notificationPublisher: null,
            profileTransferInteraction: interaction
        );
    }

    private static AppSupervisorConfig CreateConfiguration() => new()
    {
        Profiles =
        [
            new SupervisorProfileConfig
            {
                ProfileId = "existing-profile-id",
                Name = "Shared profile",
                MonitorProcess = "VRChat.exe"
            }
        ],
        Integrations = new IntegrationsConfig
        {
            HomeAssistant = new HomeAssistantIntegrationConfig
            {
                Url = "http://homeassistant.local:8123",
                Token = "home-assistant-secret"
            },
            Obs = new ObsIntegrationConfig
            {
                Host = "127.0.0.1",
                Port = 4455,
                Password = "obs-secret"
            }
        }
    };

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;

        foreach (Control child in root.Controls)
        {
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
        }
    }

    private static void Click(Button button)
    {
        MethodInfo onClick = typeof(Control).GetMethod(
            "OnClick",
            BindingFlags.Instance | BindingFlags.NonPublic
        ) ?? throw new MissingMethodException(typeof(Control).FullName, "OnClick");
        onClick.Invoke(button, [EventArgs.Empty]);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.ProfileTransferUiTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeProfileTransferInteraction : IProfileTransferInteraction
    {
        public string? ImportPath { get; set; }
        public string? ExportPath { get; set; }
        public List<ProfileTransferMessage> Messages { get; } = [];

        public string? SelectImportPath(IWin32Window owner) => ImportPath;

        public string? SelectExportPath(IWin32Window owner, string suggestedFileName) =>
            ExportPath;

        public void ShowMessage(
            IWin32Window owner,
            string text,
            string caption,
            MessageBoxIcon icon)
        {
            Messages.Add(new ProfileTransferMessage(text, caption, icon));
        }
    }

    private sealed record ProfileTransferMessage(
        string Text,
        string Caption,
        MessageBoxIcon Icon);
}
