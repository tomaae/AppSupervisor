using AppSupervisor.ConfigurationUI;
using AppSupervisor.Twitch;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

[Collection(WinFormsTestCollection.Name)]
public sealed class TwitchReauthorizationDialogTests
{
    [Fact]
    public void ReconnectButton_PerformsAuthorizationDirectly()
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                int connectionCount = 0;
                using var dialog = new TwitchReauthorizationDialog(
                    "The stored Twitch authorization can no longer be refreshed.",
                    (updateStatus, _) =>
                    {
                        connectionCount++;
                        updateStatus("Waiting for Twitch authorization...");
                        return Task.FromResult(new TwitchAuthorizationStatus(true, "broadcaster"));
                    }
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
                Assert.Equal(DialogResult.None, reconnect.DialogResult);
                Assert.Equal(DialogResult.Cancel, later.DialogResult);

                dialog.Show();
                reconnect.PerformClick();
                Application.DoEvents();

                Assert.Equal(1, connectionCount);
                Assert.Equal(DialogResult.OK, dialog.DialogResult);
                Assert.False(dialog.Visible);
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
