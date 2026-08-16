using System.Runtime.ExceptionServices;

namespace AppSupervisor.WindowsAudio;

/// <summary>Applies an audio action briefly and guarantees a best-effort restoration.</summary>
internal static class WindowsAudioActionTester
{
    public static async Task<AudioActionTestResult> RunAsync(
        IWindowsAudioController controller,
        AudioInterfaceResourceConfig configuration,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        AudioEndpointSnapshot endpoint = controller.ResolveEndpoint(configuration);
        AudioEndpointState originalState = controller.GetState(endpoint.EndpointId);
        var restoreConfiguration = new AudioInterfaceResourceConfig
        {
            EndpointId = endpoint.EndpointId,
            DeviceInstanceId = endpoint.DeviceInstanceId,
            ContainerId = endpoint.ContainerId,
            FriendlyName = endpoint.FriendlyName,
            InterfaceName = endpoint.InterfaceName,
            Direction = endpoint.Direction,
            UseDefaultDevice = false
        };
        Exception? actionFailure = null;
        bool restoreRequired = false;

        try
        {
            restoreRequired = true;
            controller.SetState(
                endpoint.EndpointId,
                new AudioEndpointState(
                    Math.Clamp(configuration.VolumePercent, 0, 100) / 100f,
                    configuration.Muted
                )
            );
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            actionFailure = ex;
        }

        if (restoreRequired)
        {
            try
            {
                AudioEndpointSnapshot restoreEndpoint = controller.ResolveEndpoint(
                    restoreConfiguration
                );
                controller.SetState(restoreEndpoint.EndpointId, originalState);
            }
            catch (Exception restoreFailure)
            {
                string message = actionFailure is null
                    ? $"The test action ran, but restoring the original audio state failed: {restoreFailure.Message}"
                    : $"The audio test failed: {actionFailure.Message} Restoration also failed: {restoreFailure.Message}";
                throw new InvalidOperationException(
                    message,
                    actionFailure is null
                        ? restoreFailure
                        : new AggregateException(actionFailure, restoreFailure)
                );
            }
        }

        if (actionFailure is not null)
            ExceptionDispatchInfo.Capture(actionFailure).Throw();

        return new AudioActionTestResult(endpoint.DisplayName, originalState);
    }
}

/// <summary>Describes a completed temporary audio-interface test.</summary>
internal sealed record AudioActionTestResult(
    string EndpointDisplayName,
    AudioEndpointState OriginalState);
