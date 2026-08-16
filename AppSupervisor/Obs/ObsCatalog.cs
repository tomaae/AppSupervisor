namespace AppSupervisor.Obs;

/// <summary>Contains OBS objects available for configuration-editor selection.</summary>
internal sealed record ObsCatalog(
    string Version,
    IReadOnlyList<string> Scenes,
    IReadOnlyList<string> AudioInputs,
    IReadOnlyList<ObsSceneSource> SceneSources);

/// <summary>Identifies one directly addressable scene item by its scene and source names.</summary>
internal sealed record ObsSceneSource(string SceneName, string SourceName);
