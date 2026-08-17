using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies the configuration editor exposes the requested process-selection and diagnostic actions.
/// </summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorFeatureSmokeTests
{
    /// <summary>Confirms process, notification, health, and Startup macro controls are constructed.</summary>
    [Fact]
    public void Constructor_FeatureControls_ArePresent()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.FeatureEditorTests-{Guid.NewGuid():N}"
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
                    Control[] controls = EnumerateControls(form).ToArray();
                    string[] buttonTexts = controls
                        .OfType<Button>()
                        .Select(button => button.Text)
                        .ToArray();
                    string[] visibleTexts = controls
                        .Select(control => control.Text)
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .ToArray();
                    string[] retiredTerms = [string.Concat("tr", "ee"), "monitoring" + " profile"];

                    Assert.Contains("Profile:", visibleTexts);
                    Assert.Equal(1, buttonTexts.Count(text => text == "Duplicate"));
                    Assert.True(buttonTexts.Count(text => text == "Browse...") >= 2);
                    Assert.True(buttonTexts.Count(text => text == "Pick running...") >= 2);
                    Assert.Contains("Pick Steam...", buttonTexts);

                    Assert.Contains("Pick Store...", buttonTexts);
                    Assert.Contains("Ensure closed until needed", visibleTexts);

                    Assert.True(buttonTexts.Count(text => text == "Test notification") >= 3);
                    Assert.Contains("Test check", buttonTexts);
                    Assert.Contains("Test action", buttonTexts);
                    Assert.Contains("Test macro", buttonTexts);
                    Assert.Contains("Startup macros", visibleTexts);
                    Assert.DoesNotContain(
                        visibleTexts,
                        text => retiredTerms.Any(term =>
                            text.Contains(term, StringComparison.OrdinalIgnoreCase))
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
                "Feature editor construction timed out."
            );
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }

    /// <summary>Recursively enumerates a WinForms control hierarchy.</summary>
    /// <param name="root">The root control.</param>
    /// <returns>The root and all descendant controls.</returns>
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
