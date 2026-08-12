using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies the code-only WinForms configuration editor can be constructed on a proper STA thread.
/// </summary>
public sealed class ConfigurationEditorSmokeTests
{
    /// <summary>Confirms a valid document builds every editor page without designer files or runtime exceptions.</summary>
    [Fact]
    public void Constructor_ValidConfiguration_CreatesEditorOnStaThread()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.EditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
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
                        cancellationToken => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
                        notificationPublisher: null
                    );
                    form.CreateControl();
                    Assert.Equal("AppSupervisor Configuration", form.Text);
                    Assert.True(form.Controls.Count >= 3);
                }
                catch (Exception ex)
                {
                    threadException = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Editor construction timed out.");
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }
}
