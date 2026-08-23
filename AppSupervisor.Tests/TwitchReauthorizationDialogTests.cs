using AppSupervisor.ConfigurationUI;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

[Collection(WinFormsTestCollection.Name)]
public sealed class TwitchReauthorizationDialogTests
{
    [Fact]
    public void Dialog_OffersExplicitReconnectAndLaterActions()
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new TwitchReauthorizationDialog(
                    "The stored Twitch authorization can no longer be refreshed."
                );
                Button reconnect = Assert.Single(
                    EnumerateControls(dialog).OfType<Button>(),
                    button => button.Text == "Reconnect Twitch"
                );
                Button later = Assert.Single(
                    EnumerateControls(dialog).OfType<Button>(),
                    button => button.Text == "Later"
                );

                Assert.Same(reconnect, dialog.AcceptButton);
                Assert.Same(later, dialog.CancelButton);
                Assert.Equal(DialogResult.OK, reconnect.DialogResult);
                Assert.Equal(DialogResult.Cancel, later.DialogResult);
                Assert.Contains(
                    EnumerateControls(dialog).OfType<Label>(),
                    label => label.Text.Contains("before the next Twitch action", StringComparison.Ordinal)
                );
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Reconnect dialog test timed out.");
        Assert.Null(threadException);
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in EnumerateControls(child))
                yield return descendant;
        }
    }
}
