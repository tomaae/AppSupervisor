using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using Microsoft.Win32;

namespace AppSupervisor;

/// <summary>
/// Integrates the structured configuration editor and verified shutdown backup with the tray lifecycle.
/// </summary>
public partial class TrayApplicationContext
{
    private bool _verifiedBackupSaved;
    private ConfigurationEditorForm? _configurationEditor;

    /// <summary>Opens one structured editor from the tray menu or tray-icon double-click and reloads an accepted document.</summary>
    /// <param name="sender">The Configure menu item or tray icon.</param>
    /// <param name="e">The menu-click or tray-icon event data.</param>
    private async void OpenConfigurationEditor(object? sender, EventArgs e)
    {
        if (_configurationEditorOpen)
        {
            if (_configurationEditor is { IsDisposed: false })
            {
                _configurationEditor.BringToFront();
                _configurationEditor.Activate();
            }

            return;
        }

        _configurationEditorOpen = true;

        try
        {
            using var editor = new ConfigurationEditorForm(
                _configPath,
                _notificationService.Publish,
                _steamVrMonitor.DiscoverAsync
            );
            _configurationEditor = editor;

            if (editor.ShowDialog(_dialogOwner) == DialogResult.OK && !_exiting)
                await LoadConfigurationAsync(showNotification: true);
        }
        finally
        {
            _configurationEditor = null;
            _configurationEditorOpen = false;
        }
    }

    /// <summary>Saves the last successfully loaded configuration to config.json.old during normal application shutdown.</summary>
    /// <param name="sender">The WinForms application lifecycle.</param>
    /// <param name="e">The application-exit event data.</param>
    private void ApplicationExiting(object? sender, EventArgs e)
    {
        _exiting = true;
        SystemEvents.PowerModeChanged -= SystemPowerModeChanged;
        Application.Idle -= ApplicationBecameIdle;
        _configurationLoadGeneration++;
        CloseAllWindows();
        SaveVerifiedConfigurationBackup();
    }

    /// <summary>Writes one shutdown backup from the verified in-memory model and never copies unvalidated disk contents.</summary>
    private void SaveVerifiedConfigurationBackup()
    {
        if (_verifiedBackupSaved || !_hasValidConfiguration)
            return;

        try
        {
            VerifiedConfigBackup.Save(_configPath, _configuration);
            _verifiedBackupSaved = true;
        }
        catch (Exception ex)
        {
            SupervisorLog.WriteError("The verified config.json.old backup could not be saved.", ex);
        }
    }
}
