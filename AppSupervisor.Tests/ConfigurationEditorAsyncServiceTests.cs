using AppSupervisor.Configuration;
using AppSupervisor.ConfigurationUI;
using AppSupervisor.ServiceControl;

namespace AppSupervisor.Tests;

/// <summary>
/// Verifies installed-service discovery does not block editor creation and is cancelled during disposal.
/// </summary>
public sealed class ConfigurationEditorAsyncServiceTests
{
    /// <summary>Confirms a pending catalog task is cancelled when an unshown editor is disposed.</summary>
    [Fact]
    public void Dispose_PendingServiceDiscovery_CancelsLoaderToken()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"AppSupervisor.AsyncEditorTests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(directoryPath);
        string configPath = Path.Combine(directoryPath, "config.json");
        ConfigFileWriter.SaveAtomic(configPath, new AppSupervisorConfig());
        CancellationToken loaderToken = default;
        Exception? threadException = null;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var pendingCatalog = new TaskCompletionSource<IReadOnlyList<InstalledServiceInfo>>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    );
                    using (var form = new ConfigurationEditorForm(
                        configPath,
                        cancellationToken =>
                        {
                            loaderToken = cancellationToken;
                            return pendingCatalog.Task;
                        },
                        notificationPublisher: null))
                    {
                        form.CreateControl();
                    }

                    Assert.True(loaderToken.IsCancellationRequested);
                    pendingCatalog.TrySetCanceled(loaderToken);
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
                "Async editor disposal test timed out."
            );
        }
        finally
        {
            Directory.Delete(directoryPath, recursive: true);
        }

        Assert.Null(threadException);
    }
}
