namespace AppSupervisor.Configuration;

/// <summary>
/// Normalizes user-entered identifiers after deserialization so matching and validation use consistent values.
/// </summary>
internal static class ConfigNormalizer
{
    /// <summary>Trims identifiers, paths, trigger values, and OSC parameter names in every non-null profile.</summary>
    public static void Normalize(IReadOnlyList<SupervisorProfileConfig?> profiles)
    {
        foreach (SupervisorProfileConfig? profile in profiles)
        {
            if (profile is null)
                continue;

            profile.ProfileId = NormalizeText(profile.ProfileId);
            profile.Name = NormalizeText(profile.Name);
            profile.MonitorProcess = NormalizeText(profile.MonitorProcess);
            if (profile.MonitorBluetoothDeviceIds is not null)
            {
                if (profile.MonitorBluetoothDeviceIds.Count == 0 &&
                    !string.IsNullOrWhiteSpace(profile.LegacyMonitorBluetoothDeviceId))
                {
                    profile.MonitorBluetoothDeviceIds.Add(
                        profile.LegacyMonitorBluetoothDeviceId
                    );
                }

                for (int index = 0; index < profile.MonitorBluetoothDeviceIds.Count; index++)
                {
                    profile.MonitorBluetoothDeviceIds[index] = NormalizeText(
                        profile.MonitorBluetoothDeviceIds[index]
                    );
                }
            }
            profile.LegacyMonitorBluetoothDeviceId = null;

            if (profile.Applications is not null)
            {
                foreach (ManagedApplicationConfig? application in profile.Applications)
                {
                    if (application is null)
                        continue;

                    NormalizeResource(application);
                    application.Path = NormalizeText(application.Path);
                    application.AppUri = NormalizeNullableText(application.AppUri);
                    application.PackageFamilyName = NormalizeNullableText(application.PackageFamilyName);
                    application.PackageApplicationId = NormalizeNullableText(application.PackageApplicationId);
                    application.PackageExecutable = NormalizeNullableText(application.PackageExecutable);

                    if (application.StartupMacros is not null)
                    {
                        foreach (StartupMacroActionConfig? action in application.StartupMacros)
                        {
                            if (action is null)
                                continue;

                            action.Monitor = NormalizeNullableText(action.Monitor);

                            if (action.Keys is not null)
                            {
                                for (int index = 0; index < action.Keys.Count; index++)
                                    action.Keys[index] = NormalizeText(action.Keys[index]);
                            }
                        }
                    }

                    if (application.HealthChecks is null)
                        continue;

                    foreach (HealthCheckConfig? healthCheck in application.HealthChecks)
                    {
                        if (healthCheck is null)
                            continue;

                        healthCheck.Name = NormalizeText(healthCheck.Name);
                        healthCheck.ActiveWhenProcess = NormalizeText(healthCheck.ActiveWhenProcess);

                        if (healthCheck.Parameters is not null)
                        {
                            for (int index = 0; index < healthCheck.Parameters.Count; index++)
                                healthCheck.Parameters[index] = NormalizeText(healthCheck.Parameters[index]);
                        }
                    }
                }
            }

            if (profile.Services is not null)
            {
                foreach (ManagedServiceConfig? service in profile.Services)
                {
                    if (service is null)
                        continue;

                    NormalizeResource(service);
                    service.ServiceName = NormalizeText(service.ServiceName);
                }
            }

            if (profile.Delays is not null)
            {
                foreach (DelayResourceConfig? delay in profile.Delays)
                {
                    if (delay is not null)
                        NormalizeResource(delay);
                }
            }

            if (profile.HomeAssistantResources is not null)
            {
                foreach (HomeAssistantResourceConfig? resource in profile.HomeAssistantResources)
                {
                    if (resource is null)
                        continue;

                    NormalizeResource(resource);
                    resource.Service = NormalizeText(resource.Service).ToLowerInvariant();
                    resource.EntityId = NormalizeText(resource.EntityId).ToLowerInvariant();
                    resource.EntityName = NormalizeText(resource.EntityName);
                }
            }

            if (profile.MqttResources is not null)
            {
                foreach (MqttResourceConfig? resource in profile.MqttResources)
                {
                    if (resource is null)
                        continue;

                    NormalizeResource(resource);
                    resource.Topic ??= "";
                    resource.Payload ??= "";
                    resource.VerificationTopic ??= "";
                    resource.ExpectedState ??= "";
                    resource.DeactivationTopic ??= "";
                    resource.DeactivationPayload ??= "";
                    resource.DeactivationExpectedState ??= "";
                }
            }

            if (profile.ObsResources is not null)
            {
                foreach (ObsResourceConfig? resource in profile.ObsResources)
                {
                    if (resource is null)
                        continue;

                    NormalizeResource(resource);
                    resource.SceneName = NormalizeText(resource.SceneName);
                    resource.InputName = NormalizeText(resource.InputName);
                    resource.SourceName = NormalizeText(resource.SourceName);
                }
            }

            if (profile.StreamDeckResources is not null)
            {
                foreach (StreamDeckResourceConfig? resource in profile.StreamDeckResources)
                {
                    if (resource is null)
                        continue;

                    NormalizeResource(resource);
                    resource.ActionId = NormalizeText(resource.ActionId);
                    resource.ActionName = NormalizeText(resource.ActionName);
                    resource.ActionTitle = NormalizeText(resource.ActionTitle);
                }
            }

            if (profile.TwitchResources is not null)
            {
                foreach (TwitchResourceConfig? resource in profile.TwitchResources)
                {
                    if (resource is null)
                        continue;
                    NormalizeResource(resource);
                    resource.Message = resource.Message?.Trim() ?? "";
                }
            }

            if (profile.AudioInterfaces is not null)
            {
                foreach (AudioInterfaceResourceConfig? resource in profile.AudioInterfaces)
                {
                    if (resource is null)
                        continue;

                    NormalizeResource(resource);
                    resource.EndpointId = NormalizeText(resource.EndpointId);
                    resource.DeviceInstanceId = NormalizeText(resource.DeviceInstanceId);
                    resource.ContainerId = NormalizeText(resource.ContainerId);
                    resource.FriendlyName = NormalizeText(resource.FriendlyName);
                    resource.InterfaceName = NormalizeText(resource.InterfaceName);
                }
            }
        }
    }

    /// <summary>Normalizes fields shared by application and service resources.</summary>
    private static void NormalizeResource(ManagedResourceConfig resource)
    {
        resource.ResourceId = NormalizeText(resource.ResourceId);
        resource.DependencyResourceId = NormalizeNullableText(resource.DependencyResourceId);
    }

    /// <summary>Returns a trimmed string and converts null to an empty value for required fields.</summary>
    private static string NormalizeText(string? value) => value?.Trim() ?? "";

    /// <summary>Returns a trimmed optional string while preserving null.</summary>
    private static string NormalizeNullableText(string? value) => value?.Trim() ?? "";
}
