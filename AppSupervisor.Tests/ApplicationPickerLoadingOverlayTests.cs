using AppSupervisor.ConfigurationUI;
using System.Drawing;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies all application pickers share one obvious loading presentation.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ApplicationPickerLoadingOverlayTests
{
    /// <summary>Confirms running, Steam, and Store pickers contain centered text and marquee activity.</summary>
    [Fact]
    public void Constructors_AllApplicationPickers_ContainLoadingOverlay()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var running = new RunningProcessPickerDialog();
                using var steam = new SteamApplicationPickerDialog();
                using var store = new StoreApplicationPickerDialog();

                AssertLoadingOverlay(running);
                AssertLoadingOverlay(steam);
                AssertLoadingOverlay(store);
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
            "Application picker loading-overlay test timed out."
        );
        Assert.Null(threadException);
    }

    private static void AssertLoadingOverlay(Form picker)
    {
        PickerLoadingOverlay overlay = Assert.Single(
            EnumerateControls(picker).OfType<PickerLoadingOverlay>()
        );
        Label label = Assert.Single(EnumerateControls(overlay).OfType<Label>());
        ProgressBar progress = Assert.Single(
            EnumerateControls(overlay).OfType<ProgressBar>()
        );
        TableLayoutPanel layout = Assert.Single(
            EnumerateControls(overlay).OfType<TableLayoutPanel>()
        );
        layout.PerformLayout();
        overlay.PerformLayout();

        Assert.Equal("Loading...", label.Text);
        Assert.True(label.Font.Bold);
        Assert.Equal(BorderStyle.FixedSingle, overlay.BorderStyle);
        Assert.Equal(ProgressBarStyle.Marquee, progress.Style);
        Assert.True(progress.MarqueeAnimationSpeed > 0);
        Assert.True(progress.Left >= layout.Padding.Left);
        Assert.True(layout.ClientSize.Width - progress.Right >= layout.Padding.Right);
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
