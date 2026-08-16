using System.Reflection;
using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.Obs;
using AppSupervisor.ServiceControl;
using AppSupervisor.SteamVr;
using AppSupervisor.WindowsAudio;
using System.Drawing;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies the OBS action editor exposes only scene-relevant controls and sources.</summary>
public sealed class ConfigurationEditorObsTests
{
    [Fact]
    public void ObsEditor_FiltersAudioBySceneAndHidesUnrelatedOptions()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.ObsEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");
        ConfigFileWriter.SaveAtomic(path, new AppSupervisorConfig
        {
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "OBS",
                    MonitorProcess = "obs64.exe",
                    ObsResources =
                    [
                        new ObsResourceConfig
                        {
                            Action = ObsActionType.SetInputMute,
                            SceneName = "Scene A",
                            InputName = "Mic A",
                            Muted = true
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
                        notificationPublisher: null,
                        steamVrDeviceLoader: _ => Task.FromResult(
                            new SteamVrSnapshot(false, null, [])
                        ),
                        obsCatalogLoader: (_, _) => Task.FromResult(new ObsCatalog(
                            "5.6.0",
                            ["Scene A", "Scene B"],
                            ["Mic A", "Mic B"],
                            [
                                new ObsSceneSource("Scene A", "Mic A"),
                                new ObsSceneSource("Scene A", "Camera A"),
                                new ObsSceneSource("Scene B", "Mic B"),
                                new ObsSceneSource("Scene B", "Camera B")
                            ]
                        )),
                        audioEndpointLoader: _ => Task.FromResult<
                            IReadOnlyList<AudioEndpointSnapshot>>([])
                    );
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    TabControl tabs = Assert.Single(form.Controls.OfType<TabControl>());
                    tabs.SelectedIndex = 1;
                    Application.DoEvents();

                    ComboBox action = GetField<ComboBox>(form, "_obsAction");
                    ComboBox scene = GetField<ComboBox>(form, "_obsScene");
                    ComboBox input = GetField<ComboBox>(form, "_obsInput");
                    ComboBox source = GetField<ComboBox>(form, "_obsSource");
                    CheckBox muted = GetField<CheckBox>(form, "_obsMuted");
                    CheckBox visible = GetField<CheckBox>(form, "_obsVisible");
                    TableLayoutPanel audioOptions = GetField<TableLayoutPanel>(
                        form,
                        "_obsAudioOptions"
                    );
                    TableLayoutPanel visibilityOptions = GetField<TableLayoutPanel>(
                        form,
                        "_obsVisibilityOptions"
                    );
                    PumpUntil(() => input.Items.Count == 1);

                    Assert.Equal(
                        ["Change scene", "Audio: Toggle mute", "Source visibility"],
                        action.Items.Cast<object>().Select(action.GetItemText)
                    );
                    Assert.Equal(["Mic A"], input.Items.Cast<string>());
                    Assert.True(muted.Checked);
                    Assert.True(audioOptions.Visible);
                    Assert.False(visibilityOptions.Visible);
                    Assert.True(ScreenTop(scene) < ScreenTop(input));

                    scene.SelectedItem = "Scene B";
                    Application.DoEvents();
                    Assert.Equal(["Mic B"], input.Items.Cast<string>());

                    action.SelectedIndex = 2;
                    Application.DoEvents();
                    Assert.False(audioOptions.Visible);
                    Assert.True(visibilityOptions.Visible);
                    Assert.Equal(["Mic B", "Camera B"], source.Items.Cast<string>());
                    Assert.True(visible.Checked);
                    Assert.True(ScreenTop(scene) < ScreenTop(source));
                    Assert.True(ScreenTop(source) < ScreenTop(visible));

                    action.SelectedIndex = 0;
                    Application.DoEvents();
                    Assert.False(audioOptions.Visible);
                    Assert.False(visibilityOptions.Visible);
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "OBS editor test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    private static T GetField<T>(ConfigurationEditorForm form, string name) where T : class
    {
        FieldInfo field = typeof(ConfigurationEditorForm).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic
        ) ?? throw new InvalidOperationException($"Missing editor field {name}.");
        return Assert.IsType<T>(field.GetValue(form));
    }

    private static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Assert.True(condition(), "OBS catalog binding did not complete.");
    }

    private static int ScreenTop(Control control) =>
        control.PointToScreen(Point.Empty).Y;
}
