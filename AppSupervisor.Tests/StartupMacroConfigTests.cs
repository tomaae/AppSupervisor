using AppSupervisor.Configuration;
using AppSupervisor.Notifications;

namespace AppSupervisor.Tests;

/// <summary>Verifies Startup macros use a strict, readable, round-trippable configuration contract.</summary>
public sealed class StartupMacroConfigTests
{
    [Fact]
    public void SerializeAndLoad_ValidMacro_PreservesOrderedActions()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"AppSupervisor.MacroConfig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.json");

        try
        {
            ConfigFileWriter.SaveAtomic(path, CreateConfiguration(
            [
                new StartupMacroActionConfig
                {
                    Type = StartupMacroActionType.Delay,
                    DelayMilliseconds = 2_000
                },
                new StartupMacroActionConfig
                {
                    Type = StartupMacroActionType.Hotkey,
                    Keys = ["ControlKey", "F5"]
                },
                new StartupMacroActionConfig
                {
                    Type = StartupMacroActionType.Hotkey,
                    Keys = ["ControlKey", "F6"]
                }
            ]));

            ManagedApplicationConfig application = Assert.Single(
                Assert.Single(ConfigLoader.Load(path).Profiles).Applications
            );

            Assert.Equal(3, application.StartupMacros.Count);
            Assert.Equal(2_000, application.StartupMacros[0].DelayMilliseconds);
            Assert.Equal(["ControlKey", "F5"], application.StartupMacros[1].Keys);
            Assert.Contains("\"startupMacros\"", File.ReadAllText(path));
            Assert.Contains("\"hotkey\"", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_HotkeyWithoutNonModifier_ReportsAction()
    {
        AppSupervisorConfig configuration = CreateConfiguration(
        [
            new StartupMacroActionConfig
            {
                Type = StartupMacroActionType.Hotkey,
                Keys = ["ControlKey", "Menu"]
            }
        ]);

        ConfigValidationException exception = Assert.Throws<ConfigValidationException>(() =>
            ConfigValidator.Validate(configuration.Profiles));

        Assert.Contains("must contain at least one non-modifier key", exception.Message);
    }

    private static AppSupervisorConfig CreateConfiguration(List<StartupMacroActionConfig> actions) =>
        new()
        {
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "Startup macro test",
                    MonitorProcess = "notepad.exe",
                    Applications =
                    [
                        new ManagedApplicationConfig
                        {
                            Path = Environment.ProcessPath!,
                            Notifications = new NotificationConfig { Target = [] },
                            StartupMacros = actions
                        }
                    ],
                    Services = []
                }
            ]
        };
}
