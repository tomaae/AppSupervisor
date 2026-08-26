using AppSupervisor.Configuration;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Abstracts the profile transfer file pickers and result messages for UI testing.</summary>
internal interface IProfileTransferInteraction
{
    string? SelectImportPath(IWin32Window owner);
    string? SelectExportPath(IWin32Window owner, string suggestedFileName);
    void ShowMessage(IWin32Window owner, string text, string caption, MessageBoxIcon icon);
}

/// <summary>Uses standard Windows dialogs for profile transfer operations.</summary>
internal sealed class ProfileTransferInteraction : IProfileTransferInteraction
{
    private const string ProfileFilter =
        "AppSupervisor profiles (*.appsupervisor-profile.json)|*.appsupervisor-profile.json|" +
        "JSON files (*.json)|*.json|All files (*.*)|*.*";

    public string? SelectImportPath(IWin32Window owner)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import AppSupervisor profile",
            Filter = ProfileFilter,
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            RestoreDirectory = true,
            ValidateNames = true,
            DereferenceLinks = true
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public string? SelectExportPath(IWin32Window owner, string suggestedFileName)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export AppSupervisor profile",
            Filter = ProfileFilter,
            FileName = suggestedFileName,
            DefaultExt = "appsupervisor-profile.json",
            AddExtension = true,
            CheckPathExists = true,
            OverwritePrompt = true,
            RestoreDirectory = true,
            ValidateNames = true,
            DereferenceLinks = true
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    public void ShowMessage(
        IWin32Window owner,
        string text,
        string caption,
        MessageBoxIcon icon)
    {
        MessageBox.Show(owner, text, caption, MessageBoxButtons.OK, icon);
    }
}
