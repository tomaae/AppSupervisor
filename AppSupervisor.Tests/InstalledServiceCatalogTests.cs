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

    /// <summary>Uses an explicit executable company name without consulting DriverStore metadata.</summary>
    [Fact]
    public void ResolvePublisher_ExecutableCompanyName_ReturnsCompanyName()
    {
        string publisher = Assert.IsType<string>(InstalledServiceCatalog.ResolvePublisher(
            "Tobii AB",
            @"C:\Program Files\Tobii\service.exe",
            @"C:\Windows\System32\DriverStore\FileRepository"
        ));

        Assert.Equal("Tobii AB", publisher);
    }

    /// <summary>Falls back to a tokenized INF provider for a DriverStore executable without company metadata.</summary>
    [Fact]
    public void ResolvePublisher_DriverStoreInfProvider_ReturnsResolvedProvider()
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor-{Guid.NewGuid():N}"
        );
        string driverStoreDirectory = Path.Combine(temporaryRoot, "FileRepository");
        string packageDirectory = Path.Combine(
            driverStoreDirectory,
            "eyetracker5.inf_amd64_test"
        );
        Directory.CreateDirectory(packageDirectory);

        try
        {
            string infPath = Path.Combine(packageDirectory, "eyetracker5.inf");
            File.WriteAllText(
                infPath,
                "[Version]\r\n" +
                "Signature=\"$Windows NT$\"\r\n" +
                "Provider=%ProviderName%\r\n" +
                "\r\n" +
                "[Strings]\r\n" +
                "ProviderName=\"Tobii AB\"\r\n"
            );

            string executablePath = Path.Combine(packageDirectory, "service.exe");
            string publisher = Assert.IsType<string>(InstalledServiceCatalog.ResolvePublisher(
                executablePublisher: null,
                executablePath,
                driverStoreDirectory
            ));

            Assert.Equal("Tobii AB", publisher);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>Does not borrow INF metadata for an executable outside the DriverStore package root.</summary>
    [Fact]
    public void ResolvePublisher_OutsideDriverStore_ReturnsNull()
    {
        string? publisher = InstalledServiceCatalog.ResolvePublisher(
            executablePublisher: null,
            @"C:\Program Files\Vendor\service.exe",
            @"C:\Windows\System32\DriverStore\FileRepository"
        );

        Assert.Null(publisher);
    }

    /// <summary>Keeps a DriverStore service when its resolved package provider is third-party.</summary>
    [Fact]
    public void IsMicrosoftOrWindowsService_ThirdPartyDriverStoreProvider_ReturnsFalse()
    {
        string executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "DriverStore",
            "FileRepository",
            "eyetracker5.inf_amd64_test",
            "service.exe"
        );

        bool filtered = InstalledServiceCatalog.IsMicrosoftOrWindowsService(
            executablePath,
            executablePath,
            "Tobii AB"
        );

        Assert.False(filtered);
    }
}
