using AppSupervisor.ServiceControl;

namespace AppSupervisor.ConfigurationUI;

/// <summary>
/// Provides asynchronous installed-service discovery and selection behavior for the structured configuration editor.
/// </summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly CancellationTokenSource _serviceRefreshCancellation = new();
    private Button _serviceRefreshButton = null!;
    private bool _serviceDiscoveryRunning;
    private bool _serviceRefreshDisposed;

    /// <summary>Builds the non-editable installed-service selector and its refresh command.</summary>
    /// <returns>A row containing the service dropdown and refresh button.</returns>
    private Control BuildServiceSelector()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _serviceName.Margin = new Padding(0, 0, 8, 0);
        _serviceRefreshButton = CreateButton("Refresh list", RefreshServicesClicked);
        _serviceRefreshButton.Margin = Padding.Empty;
        layout.Controls.Add(_serviceName, 0, 0);
        layout.Controls.Add(_serviceRefreshButton, 1, 0);
        return layout;
    }

    /// <summary>Starts a background installed-service refresh without blocking form construction.</summary>
    /// <param name="showErrors">Whether discovery errors should be displayed to the user.</param>
    private void BeginRefreshInstalledServices(bool showErrors)
    {
        _ = RefreshInstalledServicesAsync(
            showErrors,
            _serviceRefreshCancellation.Token
        );
    }

    /// <summary>
    /// Reloads installed third-party services asynchronously while preserving the currently configured service.
    /// </summary>
    /// <param name="showErrors">Whether discovery errors should be displayed to the user.</param>
    /// <param name="cancellationToken">Cancels UI updates when the editor closes.</param>
    private async Task RefreshInstalledServicesAsync(
        bool showErrors,
        CancellationToken cancellationToken)
    {
        if (_serviceDiscoveryRunning)
            return;

        string? configuredServiceName = SelectedService?.ServiceName;
        _serviceDiscoveryRunning = true;
        _serviceRefreshButton.Enabled = false;
        _serviceRefreshButton.Text = "Refreshing...";

        try
        {
            IReadOnlyList<InstalledServiceInfo> services =
                await _serviceCatalogLoader(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (IsDisposed || Disposing)
                return;

            configuredServiceName = SelectedService?.ServiceName ?? configuredServiceName;
            _installedServices = services;
            BindServiceSelector(configuredServiceName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsDisposed || Disposing)
                return;

            configuredServiceName = SelectedService?.ServiceName ?? configuredServiceName;
            _installedServices = [];
            BindServiceSelector(configuredServiceName);

            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    $"The installed service list could not be loaded. Existing configured services have been preserved.\n\n{exception.Message}",
                    "Service discovery failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
        finally
        {
            _serviceDiscoveryRunning = false;

            if (!IsDisposed && !Disposing)
            {
                _serviceRefreshButton.Enabled = true;
                _serviceRefreshButton.Text = "Refresh list";
            }
        }
    }

    /// <summary>
    /// Rebuilds the installed-service dropdown and adds a read-only placeholder for an undiscovered configured service.
    /// </summary>
    /// <param name="configuredServiceName">The service name that must remain selected and preserved.</param>
    private void BindServiceSelector(string? configuredServiceName)
    {
        bool wasLoading = _loadingControls;
        _loadingControls = true;

        try
        {
            _serviceName.Items.Clear();

            foreach (InstalledServiceInfo installedService in _installedServices)
                _serviceName.Items.Add(installedService);

            InstalledServiceInfo? selected = _installedServices.FirstOrDefault(
                service => string.Equals(
                    service.ServiceName,
                    configuredServiceName,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            if (selected is null && !string.IsNullOrWhiteSpace(configuredServiceName))
            {
                selected = new InstalledServiceInfo(
                    configuredServiceName,
                    configuredServiceName,
                    null,
                    null,
                    isConfiguredOnly: true
                );
                _serviceName.Items.Insert(0, selected);
            }

            _serviceName.SelectedItem = selected;
        }
        finally
        {
            _loadingControls = wasLoading;
        }
    }

    /// <summary>Warns only when the selected service currently uses Automatic startup.</summary>
    /// <param name="service">The service explicitly selected for supervision.</param>
    private void WarnIfAutomaticService(InstalledServiceInfo service)
    {
        if (service.IsAutomaticStart)
            _automaticServiceWarning(service);
    }

    /// <summary>Explains the persistent startup-type change before an Automatic service is applied.</summary>
    /// <param name="service">The Automatic service selected by the user.</param>
    private void ShowAutomaticServiceWarning(InstalledServiceInfo service)
    {
        string serviceDescription = string.Equals(
            service.DisplayName,
            service.ServiceName,
            StringComparison.OrdinalIgnoreCase)
            ? service.ServiceName
            : $"{service.DisplayName} ({service.ServiceName})";
        MessageBox.Show(
            this,
            $"The Windows service '{serviceDescription}' is currently set to Automatic startup.\n\n" +
            "If this service entry and its profile are enabled when you use Save & Apply, AppSupervisor will change the service startup type to Manual so it can control the service on demand.",
            "Automatic service will be changed to Manual",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }

    /// <summary>Refreshes the installed-service catalog after an explicit user request.</summary>
    /// <param name="sender">The Refresh list button.</param>
    /// <param name="e">The click event data.</param>
    private async void RefreshServicesClicked(object? sender, EventArgs e)
    {
        await RefreshInstalledServicesAsync(
            showErrors: true,
            _serviceRefreshCancellation.Token
        );
    }

    /// <summary>Cancels background service discovery and releases the form-owned resource menu.</summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeDiagnosticLogs();
            DisposeRuntimeStatus();
            DisposeHelperTesting();
            _addResourceMenu.Dispose();
            foreach (Icon icon in _resourceApplicationIcons.Values)
                icon.Dispose();
            _resourceApplicationIcons.Clear();
            if (!_bluetoothDiscoveryDisposed)
            {
                _bluetoothDiscoveryDisposed = true;
                _bluetoothDiscoveryCancellation.Cancel();
                _bluetoothDiscoveryCancellation.Dispose();
            }
        }

        if (disposing && !_serviceRefreshDisposed)
        {
            _serviceRefreshDisposed = true;
            _serviceRefreshCancellation.Cancel();
            _serviceRefreshCancellation.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Formats an installed service using its display name, internal name, publisher, and availability.</summary>
    /// <param name="sender">The installed-service dropdown.</param>
    /// <param name="e">The list formatting event data.</param>
    private void ServiceSelectorFormat(object? sender, ListControlConvertEventArgs e)
    {
        if (e.ListItem is not InstalledServiceInfo service)
            return;

        if (service.IsConfiguredOnly)
        {
            e.Value = $"{service.ServiceName} (configured; not currently available)";
            return;
        }

        string name = string.Equals(
            service.DisplayName,
            service.ServiceName,
            StringComparison.OrdinalIgnoreCase)
            ? service.ServiceName
            : $"{service.DisplayName} ({service.ServiceName})";
        e.Value = string.IsNullOrWhiteSpace(service.Publisher)
            ? name
            : $"{name} — {service.Publisher}";
    }
}
