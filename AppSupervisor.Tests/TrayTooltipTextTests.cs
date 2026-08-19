namespace AppSupervisor.Tests;

/// <summary>Verifies tray error text identifies the failure while respecting native tooltip limits.</summary>
public sealed class TrayTooltipTextTests
{
    [Fact]
    public void CreateErrorSummary_CollapsesMultilineDetail()
    {
        string summary = TrayTooltipText.CreateErrorSummary(
            "VRChat - XSOverlay",
            "Could not start.\r\nAccess was denied."
        );

        Assert.Equal(
            "VRChat - XSOverlay: Could not start. Access was denied.",
            summary
        );
    }

    [Fact]
    public void FormatError_ShowsConcreteFailureInsteadOfGenericStatus()
    {
        string text = TrayTooltipText.FormatError(
            "VRChat - XSOverlay: Process exited before startup completed.",
            additionalErrorCount: 0
        );

        Assert.Equal(
            "AppSupervisor - VRChat - XSOverlay: Process exited before startup completed.",
            text
        );
        Assert.DoesNotContain("Supervision error", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateErrorSummary_EmptyExceptionMessage_KeepsSpecificCategory()
    {
        string summary = TrayTooltipText.CreateErrorSummary(
            "Profile lifecycle failed",
            "  "
        );

        Assert.Equal("Profile lifecycle failed", summary);
    }

    [Fact]
    public void FormatError_PreservesAdditionalCountAndLifecycleActivity()
    {
        string text = TrayTooltipText.FormatError(
            new string('x', 200),
            additionalErrorCount: 2,
            activity: "starting helpers"
        );

        Assert.Equal(TrayTooltipText.MaximumLength, text.Length);
        Assert.EndsWith(" (+2 more); starting helpers", text, StringComparison.Ordinal);
        Assert.Contains('…', text);
    }
}
