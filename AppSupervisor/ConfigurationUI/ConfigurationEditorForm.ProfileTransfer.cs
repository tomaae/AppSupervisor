using AppSupervisor.Configuration;

namespace AppSupervisor.ConfigurationUI;

/// <summary>Integrates portable profile import and export commands into the configuration editor.</summary>
public sealed partial class ConfigurationEditorForm
{
    private readonly IProfileTransferInteraction _profileTransferInteraction;

    private void ExportProfileClicked(object? sender, EventArgs e)
    {
        if (SelectedProfile is not SupervisorProfileConfig selected)
            return;

        string? path = _profileTransferInteraction.SelectExportPath(
            this,
            ProfileTransferService.CreateSuggestedFileName(selected.Name)
        );

        if (path is null)
            return;

        try
        {
            ProfileExportResult result = ProfileTransferService.SaveAtomic(path, selected);
            _profileTransferInteraction.ShowMessage(
                this,
                BuildTransferMessage(
                    $"Profile '{DisplayName(selected.Name, "Unnamed profile")}' was exported.",
                    result.Warnings,
                    "Application-wide integration credentials and connection settings were not included."
                ),
                "Profile exported",
                MessageBoxIcon.Information
            );
        }
        catch (Exception exception)
        {
            ShowProfileTransferError("The profile could not be exported.", exception);
        }
    }

    private void ImportProfileClicked(object? sender, EventArgs e)
    {
        string? path = _profileTransferInteraction.SelectImportPath(this);

        if (path is null)
            return;

        try
        {
            ProfileImportResult result = ProfileTransferService.Load(path, _profiles);
            _profiles.Add(result.Profile);
            BindProfileSelector(result.Profile);
            UpdateStatus();

            string renamed = result.NameChanged
                ? $" The imported name was changed to '{result.Profile.Name}' to avoid a duplicate."
                : "";
            _profileTransferInteraction.ShowMessage(
                this,
                BuildTransferMessage(
                    $"Profile '{result.Profile.Name}' was imported as a new disabled profile.{renamed}",
                    result.Warnings,
                    "Review the preserved computer-specific values, then enable and Save & Apply when ready."
                ),
                "Profile imported",
                MessageBoxIcon.Information
            );
        }
        catch (Exception exception)
        {
            ShowProfileTransferError("The profile could not be imported.", exception);
        }
    }

    private void ShowProfileTransferError(string introduction, Exception exception)
    {
        _profileTransferInteraction.ShowMessage(
            this,
            $"{introduction}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
            "Profile transfer error",
            MessageBoxIcon.Error
        );
    }

    private static string BuildTransferMessage(
        string introduction,
        IReadOnlyList<string> warnings,
        string conclusion)
    {
        var builder = new System.Text.StringBuilder(introduction);

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("Review these portable-profile warnings:");

            foreach (string warning in warnings)
                builder.AppendLine($"• {warning}");
        }

        builder.AppendLine();
        builder.AppendLine();
        builder.Append(conclusion);
        return builder.ToString();
    }
}
