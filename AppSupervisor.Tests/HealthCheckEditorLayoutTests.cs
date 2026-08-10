using AppSupervisor.ConfigurationUI;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies the health-check editor keeps its groups, field columns, and notification content aligned.</summary>
public sealed class HealthCheckEditorLayoutTests
{
    /// <summary>Confirms VRCOSC guidance describes root structure and strict-majority freshness accurately.</summary>
    [Fact]
    public void Constructor_VrcOscGuidance_UsesCurrentBehavior()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new HealthCheckEditorDialog(new HealthCheckConfig
                {
                    Name = "OSCQuery check",
                    Type = HealthCheckType.Vrcosc,
                    Parameters = ["JawOpen", "JawX"],
                    StaleSeconds = 20,
                    Notifications = new NotificationConfig { Target = [] }
                });
                string[] labels = EnumerateControls(form)
                    .OfType<Label>()
                    .Select(label => label.Text)
                    .ToArray();

                Assert.Contains(
                    labels,
                    text => text.Contains("root address structure", StringComparison.Ordinal)
                );
                Assert.Contains(
                    labels,
                    text => text.Contains("strict majority", StringComparison.Ordinal)
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
            "VRCOSC guidance verification timed out."
        );

        Assert.Null(threadException);
    }

    /// <summary>Confirms the laid-out dialog uses one group width and keeps notifications below their caption.</summary>
    [Fact]
    public void Constructor_VisibleLayout_AlignsGroupsAndEditors()
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new HealthCheckEditorDialog(new HealthCheckConfig
                {
                    Name = "Layout check",
                    Type = HealthCheckType.Listener,
                    Protocol = ListenerProtocol.Tcp,
                    Port = 12345,
                    Notifications = new NotificationConfig { Target = [] }
                });
                form.ShowInTaskbar = false;
                form.Opacity = 0;
                form.Show();
                Application.DoEvents();
                form.PerformLayout();

                TableLayoutPanel content = form.Controls
                    .OfType<TableLayoutPanel>()
                    .Single(panel => panel.AutoScroll);
                GroupBox[] groups = content.Controls.OfType<GroupBox>().ToArray();
                GroupBox[] visibleGroups = groups.Where(group => group.Visible).ToArray();

                Assert.Equal(4, groups.Length);
                Assert.Equal(3, visibleGroups.Length);
                Assert.Single(visibleGroups.Select(group => group.Width).Distinct());

                GroupBox general = groups.Single(group => group.Text == "General");
                TableLayoutPanel settings = general.Controls
                    .OfType<TableLayoutPanel>()
                    .Single();
                int[] editorLeftEdges = settings.Controls
                    .Cast<Control>()
                    .Where(control => settings.GetColumn(control) == 1)
                    .Select(control => control.Left)
                    .Distinct()
                    .ToArray();
                Assert.Single(editorLeftEdges);

                GroupBox notifications = groups.Single(group => group.Text == "Notifications");
                Control notificationContent = Assert.Single(
                    notifications.Controls.Cast<Control>()
                );
                Assert.True(
                    notificationContent.Top >= notifications.DisplayRectangle.Top,
                    "Notification choices must start below the group-box caption."
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
            "Health-check editor layout verification timed out."
        );

        Assert.Null(threadException);
    }

    /// <summary>Recursively enumerates one control and every descendant.</summary>
    /// <param name="root">The root control.</param>
    /// <returns>The complete control hierarchy.</returns>
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
