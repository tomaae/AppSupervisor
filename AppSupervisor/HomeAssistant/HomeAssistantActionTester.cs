namespace AppSupervisor.HomeAssistant;

/// <summary>Safely previews one reversible Home Assistant resource action.</summary>
internal static class HomeAssistantActionTester
{
    private static readonly TimeSpan PreviewDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads the current entity state, applies a state-changing action for five seconds, verifies
    /// its result, and requests restoration of the original state.
    /// </summary>
    /// <param name="client">The authenticated Home Assistant client.</param>
    /// <param name="service">The configured stateful service.</param>
    /// <param name="entityId">The configured entity identifier.</param>
    /// <param name="cancellationToken">Cancels preparation before the action is applied.</param>
    /// <returns>A result describing whether a state-changing preview was required.</returns>
    public static Task<HomeAssistantActionTestResult> RunAsync(
        IHomeAssistantClient client,
        string service,
        string entityId,
        CancellationToken cancellationToken)
    {
        return RunAsync(
            client,
            service,
            entityId,
            (delay, token) => Task.Delay(delay, token),
            cancellationToken
        );
    }

    /// <summary>Runs a reversible preview with an injectable delay for deterministic tests.</summary>
    /// <param name="client">The authenticated Home Assistant client.</param>
    /// <param name="service">The configured stateful service.</param>
    /// <param name="entityId">The configured entity identifier.</param>
    /// <param name="delay">The delay implementation used between applying and restoring the action.</param>
    /// <param name="cancellationToken">Cancels preparation before the action is applied.</param>
    /// <returns>A result describing whether a state-changing preview was required.</returns>
    internal static async Task<HomeAssistantActionTestResult> RunAsync(
        IHomeAssistantClient client,
        string service,
        string entityId,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(delay);

        string? desiredState = HomeAssistantServiceSemantics.GetDesiredState(service);
        string? reverseService = HomeAssistantServiceSemantics.GetReverseService(service);

        if (desiredState is null || reverseService is null)
        {
            throw new InvalidOperationException(
                $"'{service}' is stateless and cannot be tested safely because its effect cannot be reverted."
            );
        }

        string originalState = await client.GetEntityStateAsync(entityId, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(originalState, desiredState, StringComparison.OrdinalIgnoreCase))
        {
            return new HomeAssistantActionTestResult(
                Changed: false,
                OriginalState: originalState,
                DesiredState: desiredState
            );
        }

        if (!string.Equals(originalState, "on", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(originalState, "off", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{entityId}' is currently '{originalState}', so AppSupervisor cannot safely restore its original state after the test."
            );
        }

        bool actionApplied = false;
        Exception? previewFailure = null;

        try
        {
            await client.CallServiceAsync(service, entityId, cancellationToken)
                .ConfigureAwait(false);
            actionApplied = true;
            await delay(PreviewDuration, CancellationToken.None).ConfigureAwait(false);
            string observedState = await client.GetEntityStateAsync(
                entityId,
                CancellationToken.None
            ).ConfigureAwait(false);

            if (!string.Equals(observedState, desiredState, StringComparison.OrdinalIgnoreCase))
            {
                previewFailure = new InvalidOperationException(
                    $"'{entityId}' became '{observedState}' instead of '{desiredState}'."
                );
            }
        }
        catch (Exception ex)
        {
            previewFailure = ex;
        }

        if (actionApplied)
        {
            try
            {
                await client.CallServiceAsync(reverseService, entityId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception restorationFailure)
            {
                throw new InvalidOperationException(
                    $"The test could not restore '{entityId}' to its original '{originalState}' state. Restore it manually. {restorationFailure.Message}",
                    restorationFailure
                );
            }
        }

        if (previewFailure is not null)
        {
            throw new InvalidOperationException(
                $"The test action did not produce the expected '{desiredState}' state. The original '{originalState}' state was requested again. {previewFailure.Message}",
                previewFailure
            );
        }

        return new HomeAssistantActionTestResult(
            Changed: true,
            OriginalState: originalState,
            DesiredState: desiredState
        );
    }
}

/// <summary>Describes the observable result of a reversible Home Assistant action preview.</summary>
/// <param name="Changed">Whether the test temporarily changed the entity.</param>
/// <param name="OriginalState">The state read before the test.</param>
/// <param name="DesiredState">The state requested by the configured service.</param>
internal sealed record HomeAssistantActionTestResult(
    bool Changed,
    string OriginalState,
    string DesiredState
);
