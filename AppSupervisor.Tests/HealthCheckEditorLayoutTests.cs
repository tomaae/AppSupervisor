using AppSupervisor.ConfigurationUI;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies the health-check editor keeps its groups, field columns, and notification content aligned.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class HealthCheckEditorLayoutTests
{
    /// <summary>Confirms the VRCOSC picker follows live VRChat process availability.</summary>
    /// <param name="vrChatRunning">Whether the injected process observation reports VRChat.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_ParameterPicker_EnabledOnlyWhileVrChatRuns(bool vrChatRunning)
    {
        RunInSta(() =>
        {
            using var form = new HealthCheckEditorDialog(
                new HealthCheckConfig
                {
                    Name = "OSCQuery check",
                    Type = HealthCheckType.Vrcosc,
                    Notifications = new NotificationConfig { Target = [] }
                },
                () => vrChatRunning,
                _ => Task.FromResult<IReadOnlyList<string>>([])
            );
            Button picker = Assert.Single(
                EnumerateControls(form).OfType<Button>(),
                button => button.Text == "Pick..."
            );

            Assert.Equal(vrChatRunning, picker.Enabled);
        });
    }

    /// <summary>Confirms configured available parameters are visibly checked in the picker.</summary>
    [Fact]
    public void Constructor_ParameterPicker_MarksConfiguredParameters()
    {
        RunInSta(() =>
        {
            using var picker = new VrcOscParameterPickerDialog(
                ["EyeLeft", "JawOpen"],
                ["JawOpen", "UnavailableOnAvatar"]
            );
            CheckedListBox list = Assert.Single(
                EnumerateControls(picker).OfType<CheckedListBox>()
            );

            Assert.Equal(2, list.Items.Count);
            Assert.Equal(["JawOpen"], list.CheckedItems.Cast<string>());
        });
    }

    /// <summary>Confirms applying current-avatar choices cannot erase configured unavailable names.</summary>
    [Fact]
    public void MergeParameterSelections_UnavailableConfiguredName_PreservesIt()
    {
        IReadOnlyList<string> merged = HealthCheckEditorDialog.MergeParameterSelections(
            ["JawOpen", "UnavailableOnAvatar"],
            ["EyeLeft", "JawOpen"],
            ["EyeLeft"]
        );

        Assert.Equal(["UnavailableOnAvatar", "EyeLeft"], merged);
    }

    /// <summary>Confirms VRCOSC guidance stays limited to actionable parameter and freshness details.</summary>
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
                Assert.DoesNotContain(
                    labels,
                    text => text.Contains("Automatic OSCQuery requests", StringComparison.Ordinal)
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
    /// <param name="type">The health-check view whose visible groups are verified.</param>
    [Theory]
    [InlineData(HealthCheckType.Listener)]
    [InlineData(HealthCheckType.Vrcosc)]
    public void Constructor_VisibleLayout_AlignsGroupsAndEditors(HealthCheckType type)
    {
        Exception? threadException = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new HealthCheckEditorDialog(new HealthCheckConfig
                {
                    Name = "Layout check",
                    Type = type,
                    Protocol = ListenerProtocol.Tcp,
                    Port = 12345,
                    Parameters = ["JawOpen", "JawX"],
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
                ComboBox typeSelector = Assert.Single(
                    settings.Controls.OfType<ComboBox>(),
                    comboBox => comboBox.SelectedItem is HealthCheckType
                );
                Assert.Equal(DrawMode.OwnerDrawFixed, typeSelector.DrawMode);
                Assert.Equal(
                    ConfigurationIconListRenderer.GetItemHeight(typeSelector),
                    typeSelector.ItemHeight
                );
                int[] editorLeftEdges = settings.Controls
                    .Cast<Control>()
                    .Where(control => settings.GetColumn(control) == 1)
                    .Select(control => control.Left)
                    .Distinct()
                    .ToArray();
                Assert.Single(editorLeftEdges);

                if (type == HealthCheckType.Listener)
                {
                    GroupBox listener = groups.Single(group => group.Text == "Listener");
                    Control[] listenerControls = EnumerateControls(listener).ToArray();
                    ComboBox protocol = Assert.Single(
                        listenerControls.OfType<ComboBox>(),
                        combo => combo.SelectedItem is ListenerProtocol
                    );
                    NumericUpDown port = Assert.Single(
                        listenerControls.OfType<NumericUpDown>()
                    );
                    TextBox process = Assert.Single(
                        listenerControls.OfType<TextBox>(),
                        textBox => !textBox.Multiline && textBox.Parent is not NumericUpDown
                    );
                    Assert.Equal(OuterLeft(protocol), OuterLeft(port));
                    Assert.Equal(OuterLeft(protocol), OuterLeft(process));
                }

                GroupBox notifications = groups.Single(group => group.Text == "Notifications");
                Control notificationContent = Assert.Single(
                    notifications.Controls.Cast<Control>()
                );
                Assert.True(
                    notificationContent.Top >= notifications.DisplayRectangle.Top,
                    "Notification choices must start below the group-box caption."
                );
                Assert.InRange(
                    notifications.DisplayRectangle.Bottom - notificationContent.Bottom,
                    0,
                    24
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

    private static int OuterLeft(Control control) =>
        control.Parent!.PointToScreen(control.Location).X;

    /// <summary>Runs one WinForms assertion on a bounded STA thread.</summary>
    private static void RunInSta(Action assertion)
    {
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                assertion();
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WinForms assertion timed out.");
        Assert.Null(threadException);
    }
}
