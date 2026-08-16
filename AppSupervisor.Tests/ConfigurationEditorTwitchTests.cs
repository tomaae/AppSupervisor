using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using AppSupervisor.Twitch;
using System.Reflection;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Protects Twitch action-specific editor visibility and selector layout.</summary>
public sealed class ConfigurationEditorTwitchTests
{
    [Fact]
    public void ConnectionStatus_EnablesOnlyApplicableConnectionButton()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.TwitchConnectionEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig());
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
                    MethodInfo applyStatus = typeof(ConfigurationEditorForm).GetMethod(
                        "ApplyTwitchConnectionStatus",
                        BindingFlags.Instance | BindingFlags.NonPublic
                    )!;
                    Button connect = Assert.Single(
                        EnumerateControls(form).OfType<Button>(),
                        button => button.Text == "Connect Twitch"
                    );
                    Button disconnect = Assert.Single(
                        EnumerateControls(form).OfType<Button>(),
                        button => button.Text == "Disconnect"
                    );

                    applyStatus.Invoke(form, [new TwitchAuthorizationStatus(true, "broadcaster")]);

                    Assert.False(connect.Enabled);
                    Assert.True(disconnect.Enabled);

                    applyStatus.Invoke(form, [TwitchAuthorizationStatus.Disconnected]);

                    Assert.True(connect.Enabled);
                    Assert.False(disconnect.Enabled);
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
                "Twitch connection-button editor test timed out."
            );
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    [Fact]
    public void ActionSelection_ShowsOnlyApplicableModeRowsAndDoesNotClipAdLength()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.TwitchEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig
        {
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "Stream",
                    MonitorProcess = "obs64.exe",
                    TwitchResources =
                    [
                        new TwitchResourceConfig { Action = TwitchActionType.EmoteOnly }
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
                        configPath,
                        _ => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
                        notificationPublisher: null
                    );
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                    tabs.SelectedTab = Assert.Single(
                        tabs.TabPages.Cast<TabPage>(),
                        page => page.Text == "Resources"
                    );
                    Application.DoEvents();
                    Control[] controls = EnumerateControls(form).ToArray();
                    ComboBox action = Assert.Single(
                        controls.OfType<ComboBox>(),
                        combo => combo.Items.Cast<object>()
                            .Select(combo.GetItemText)
                            .Contains("Emote-only chat")
                    );
                    Label minimumFollow = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Text == "Minimum follow"
                    );
                    Label messageInterval = Assert.Single(
                        controls.OfType<Label>(),
                        label => label.Text == "Message interval"
                    );

                    SelectAction(action, "Emote-only chat");
                    Assert.False(minimumFollow.Visible);
                    Assert.False(messageInterval.Visible);

                    SelectAction(action, "Followers-only chat");
                    Assert.True(minimumFollow.Visible);
                    Assert.False(messageInterval.Visible);

                    SelectAction(action, "Slow chat");
                    Assert.False(minimumFollow.Visible);
                    Assert.True(messageInterval.Visible);

                    SelectAction(action, "Subscribers-only chat");
                    Assert.False(minimumFollow.Visible);
                    Assert.False(messageInterval.Visible);

                    SelectAction(action, "Play advertisement");
                    ComboBox adLength = Assert.Single(
                        controls.OfType<ComboBox>(),
                        combo => combo.Items.Count == 6 &&
                            combo.Items.Cast<object>().All(item => item is int)
                    );
                    Assert.True(adLength.Visible);
                    Assert.NotNull(adLength.Parent);
                    Assert.True(
                        adLength.Bottom + adLength.Parent.Padding.Bottom <=
                            adLength.Parent.ClientSize.Height,
                        "The ad-length selector must retain bottom clearance inside its layout panel."
                    );
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Twitch editor test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    private static void SelectAction(ComboBox selector, string displayName)
    {
        selector.SelectedItem = Assert.Single(
            selector.Items.Cast<object>(),
            item => selector.GetItemText(item) == displayName
        );
        Application.DoEvents();
        selector.FindForm()?.PerformLayout();
        Application.DoEvents();
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
    }
}
