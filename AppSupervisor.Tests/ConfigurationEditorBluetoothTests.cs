using AppSupervisor.Bluetooth;
using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;
using System.Windows.Forms;

namespace AppSupervisor.Tests;

/// <summary>Verifies global Bluetooth registration and profile trigger controls in the editor.</summary>
[Collection(WinFormsTestCollection.Name)]
public sealed class ConfigurationEditorBluetoothTests
{
    [Fact]
    public void Constructor_BluetoothControlsAndConfiguredTrigger_ArePresent()
    {
        string directory = CreateConfiguration(out string configPath);
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var form = CreateForm(configPath, new StubScanner([]));
                    form.CreateControl();
                    Control[] controls = EnumerateControls(form).ToArray();

                    Assert.Contains(
                        controls.OfType<Button>(),
                        button => button.Text == "Discover nearby devices"
                    );
                    Assert.Contains(
                        controls.OfType<Label>(),
                        label => label.Text == "Activation trigger"
                    );
                    ComboBox triggerSelector = Assert.Single(
                        controls.OfType<ComboBox>(),
                        combo => combo.Items.Cast<object>().OfType<ProfileTriggerType>().Any()
                    );
                    Assert.Equal(ProfileTriggerType.BluetoothDevice, triggerSelector.SelectedItem);
                    Assert.Contains(
                        controls.OfType<DataGridView>(),
                        grid => grid.Columns.Cast<DataGridViewColumn>().Any(column =>
                            column.HeaderText == "Registered name")
                    );
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Bluetooth editor test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    [Fact]
    public void DiscoverButton_AddsNewDeviceToGlobalRegistryGrid()
    {
        string directory = CreateConfiguration(out string configPath);
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var scanner = new StubScanner(
                    [
                        new BluetoothDeviceSnapshot(
                            "windows-speaker",
                            "Speaker",
                            "112233445566",
                            BluetoothDeviceKind.Classic,
                            IsPaired: false,
                            IsConnected: false,
                            IsPresent: true
                        )
                    ]);
                    using var form = CreateForm(configPath, scanner);
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();
                    TabControl tabs = Assert.Single(EnumerateControls(form).OfType<TabControl>());
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>()
                        .Single(page => page.Text == "Integrations");
                    Application.DoEvents();
                    Button discover = EnumerateControls(form).OfType<Button>()
                        .Single(button => button.Text == "Discover nearby devices");
                    DataGridView grid = EnumerateControls(form).OfType<DataGridView>()
                        .Single(candidate => candidate.Columns.Cast<DataGridViewColumn>()
                            .Any(column => column.HeaderText == "Registered name"));

                    discover.PerformClick();
                    DateTime timeoutUtc = DateTime.UtcNow.AddSeconds(3);
                    while ((!discover.Enabled || grid.Rows.Count < 2) && DateTime.UtcNow < timeoutUtc)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }

                    Assert.Equal(1, scanner.CallCount);
                    Assert.Equal(2, grid.Rows.Count);
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Bluetooth discovery editor test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    [Fact]
    public void DiscoverButton_ShowsBluetoothLoadingOverlayUntilDiscoveryCompletes()
    {
        string directory = CreateConfiguration(out string configPath);
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var scanner = new BlockingScanner();
                    using var form = CreateForm(configPath, scanner);
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();
                    TabControl tabs = Assert.Single(EnumerateControls(form).OfType<TabControl>());
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>()
                        .Single(page => page.Text == "Integrations");
                    Application.DoEvents();
                    Button discover = EnumerateControls(form).OfType<Button>()
                        .Single(button => button.Text == "Discover nearby devices");
                    PickerLoadingOverlay overlay = Assert.Single(
                        EnumerateControls(form).OfType<PickerLoadingOverlay>()
                    );

                    Assert.False(overlay.Visible);
                    discover.PerformClick();
                    Application.DoEvents();

                    Assert.True(overlay.Visible);
                    Assert.False(discover.Enabled);
                    Assert.Equal(
                        "Looking for Bluetooth devices...",
                        Assert.Single(EnumerateControls(overlay).OfType<Label>()).Text
                    );
                    Assert.Equal(
                        ProgressBarStyle.Marquee,
                        Assert.Single(EnumerateControls(overlay).OfType<ProgressBar>()).Style
                    );

                    scanner.Complete(
                    [
                        new BluetoothDeviceSnapshot(
                            "windows-speaker",
                            "Speaker",
                            "112233445566",
                            BluetoothDeviceKind.Classic,
                            IsPaired: false,
                            IsConnected: false,
                            IsPresent: true
                        )
                    ]);
                    DateTime timeoutUtc = DateTime.UtcNow.AddSeconds(3);
                    while ((!discover.Enabled || overlay.Visible) && DateTime.UtcNow < timeoutUtc)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }

                    Assert.True(discover.Enabled);
                    Assert.False(overlay.Visible);
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Bluetooth loading-overlay test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    [Fact]
    public void DiscoverButton_RegistersUnnamedDeviceWithExplicitPlaceholder()
    {
        string directory = CreateConfiguration(out string configPath);
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var scanner = new StubScanner(
                    [
                        new BluetoothDeviceSnapshot(
                            "windows-speaker",
                            "Speaker",
                            "112233445566",
                            BluetoothDeviceKind.Classic,
                            IsPaired: false,
                            IsConnected: false,
                            IsPresent: true
                        ),
                        new BluetoothDeviceSnapshot(
                            "anonymous-beacon",
                            "4C2299AC31BA",
                            "4C2299AC31BA",
                            BluetoothDeviceKind.LowEnergy,
                            IsPaired: false,
                            IsConnected: false,
                            IsPresent: true,
                            SignalStrengthDbm: -60,
                            ManufacturerCompanyIds: [76]
                        )
                    ]);
                    using var form = CreateForm(configPath, scanner);
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();
                    TabControl tabs = Assert.Single(EnumerateControls(form).OfType<TabControl>());
                    tabs.SelectedTab = tabs.TabPages.Cast<TabPage>()
                        .Single(page => page.Text == "Integrations");
                    Application.DoEvents();
                    Button discover = EnumerateControls(form).OfType<Button>()
                        .Single(button => button.Text == "Discover nearby devices");
                    DataGridView grid = EnumerateControls(form).OfType<DataGridView>()
                        .Single(candidate => candidate.Columns.Cast<DataGridViewColumn>()
                            .Any(column => column.HeaderText == "Registered name"));

                    discover.PerformClick();
                    DateTime timeoutUtc = DateTime.UtcNow.AddSeconds(3);
                    while (!discover.Enabled && DateTime.UtcNow < timeoutUtc)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }

                    Assert.Equal(3, grid.Rows.Count);
                    Assert.Contains(
                        grid.Rows.Cast<DataGridViewRow>(),
                        row => string.Equals(
                            row.Cells[0].Value?.ToString(),
                            "Unidentified LE device (31BA)",
                            StringComparison.Ordinal
                        )
                    );
                    int proximityColumnIndex = grid.Columns.Cast<DataGridViewColumn>()
                        .Single(column => column.HeaderText == "Proximity estimate")
                        .Index;
                    Assert.Contains(
                        grid.Rows.Cast<DataGridViewRow>(),
                        row => string.Equals(
                            row.Cells[proximityColumnIndex].Value?.ToString(),
                            "Near",
                            StringComparison.Ordinal
                        )
                    );
                    int manufacturerColumnIndex = grid.Columns.Cast<DataGridViewColumn>()
                        .Single(column => column.HeaderText == "Manufacturer")
                        .Index;
                    Assert.Contains(
                        grid.Rows.Cast<DataGridViewRow>(),
                        row => string.Equals(
                            row.Cells[manufacturerColumnIndex].Value?.ToString(),
                            "Apple, Inc.",
                            StringComparison.Ordinal
                        )
                    );
                }
                catch (Exception exception)
                {
                    threadException = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Bluetooth discovery editor test timed out.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Null(threadException);
    }

    private static ConfigurationEditorForm CreateForm(
        string configPath,
        IBluetoothDeviceScanner scanner) => new(
            configPath,
            cancellationToken => Task.FromResult<IReadOnlyList<InstalledServiceInfo>>([]),
            notificationPublisher: null,
            bluetoothDeviceScanner: scanner
        );

    private static string CreateConfiguration(out string configPath)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.BluetoothEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directory);
        configPath = Path.Combine(directory, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig
        {
            Integrations = new IntegrationsConfig
            {
                Bluetooth = new BluetoothIntegrationConfig
                {
                    Devices =
                    [
                        new BluetoothDeviceConfig
                        {
                            DeviceId = "phone-id",
                            Name = "Phone",
                            Address = "AABBCCDDEEFF",
                            Kind = BluetoothDeviceKind.LowEnergy
                        }
                    ]
                }
            },
            Profiles =
            [
                new SupervisorProfileConfig
                {
                    Name = "Phone presence",
                    TriggerType = ProfileTriggerType.BluetoothDevice,
                    MonitorBluetoothDeviceId = "phone-id"
                }
            ]
        });
        return directory;
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

    private sealed class StubScanner(
        IReadOnlyList<BluetoothDeviceSnapshot> result) : IBluetoothDeviceScanner
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<IReadOnlyList<BluetoothDeviceSnapshot>> DiscoverAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingScanner : IBluetoothDeviceScanner
    {
        private readonly TaskCompletionSource<IReadOnlyList<BluetoothDeviceSnapshot>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<BluetoothDeviceSnapshot>> DiscoverAsync(
            CancellationToken cancellationToken) => _completion.Task.WaitAsync(cancellationToken);

        public void Complete(IReadOnlyList<BluetoothDeviceSnapshot> result) =>
            _completion.TrySetResult(result);
    }
}
