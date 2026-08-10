using AppSupervisor.ServiceControl;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies service command-line parsing and deterministic Microsoft/Windows filtering rules.
/// </summary>
public sealed class InstalledServiceCatalogTests
{
    /// <summary>Extracts a quoted executable path without retaining service arguments.</summary>
    [Fact]
    public void ExtractExecutablePath_QuotedPathWithArguments_ReturnsExecutable()
    {
        string expected = Path.GetFullPath(@"C:\Program Files\Vendor\Helper Service.exe");

        string? actual = InstalledServiceCatalog.ExtractExecutablePath(
            "\"C:\\Program Files\\Vendor\\Helper Service.exe\" --service"
        );

        Assert.Equal(expected, actual);
    }

    /// <summary>Extracts an unquoted executable path containing spaces through its .exe suffix.</summary>
    [Fact]
    public void ExtractExecutablePath_UnquotedPathWithSpaces_ReturnsExecutable()
    {
        string expected = Path.GetFullPath(@"C:\Program Files\Vendor\Helper Service.exe");

        string? actual = InstalledServiceCatalog.ExtractExecutablePath(
            @"C:\Program Files\Vendor\Helper Service.exe --service"
        );

        Assert.Equal(expected, actual);
    }

    /// <summary>Resolves the native SystemRoot alias used by built-in Windows service commands.</summary>
    [Fact]
    public void ExtractExecutablePath_SystemRootAlias_ResolvesWindowsDirectory()
    {
        string expected = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "svchost.exe"
        ));

        string? actual = InstalledServiceCatalog.ExtractExecutablePath(
            @"\SystemRoot\System32\svchost.exe -k LocalService"
        );

        Assert.Equal(expected, actual);
    }

    /// <summary>Filters executables whose version publisher identifies Microsoft.</summary>
    [Fact]
    public void IsMicrosoftOrWindowsService_MicrosoftPublisher_ReturnsTrue()
    {
        bool filtered = InstalledServiceCatalog.IsMicrosoftOrWindowsService(
            @"C:\Program Files\Example\service.exe",
            @"C:\Program Files\Example\service.exe",
            "Microsoft Corporation"
        );

        Assert.True(filtered);
    }

    /// <summary>Filters standard Windows service host processes even outside a Windows-directory spelling.</summary>
    [Fact]
    public void IsMicrosoftOrWindowsService_ServiceHost_ReturnsTrue()
    {
        bool filtered = InstalledServiceCatalog.IsMicrosoftOrWindowsService(
            @"C:\Windows\System32\svchost.exe -k netsvcs",
            @"C:\Windows\System32\svchost.exe",
            null
        );

        Assert.True(filtered);
    }

    /// <summary>Filters unidentified executables hosted under the Windows installation directory.</summary>
    [Fact]
    public void IsMicrosoftOrWindowsService_WindowsPathWithoutPublisher_ReturnsTrue()
    {
        string executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "built-in-service.exe"
        );

        bool filtered = InstalledServiceCatalog.IsMicrosoftOrWindowsService(
            executablePath,
            executablePath,
            null
        );

        Assert.True(filtered);
    }

    /// <summary>Keeps a third-party publisher installed outside the Windows directory.</summary>
    [Fact]
    public void IsMicrosoftOrWindowsService_ThirdPartyProgramFilesService_ReturnsFalse()
    {
        bool filtered = InstalledServiceCatalog.IsMicrosoftOrWindowsService(
            @"C:\Program Files\HTC\VIVE\ViveAgentService.exe",
            @"C:\Program Files\HTC\VIVE\ViveAgentService.exe",
            "HTC Corporation"
        );

        Assert.False(filtered);
    }
}
