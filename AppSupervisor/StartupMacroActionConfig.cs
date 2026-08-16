using System.Text.Json.Serialization;

namespace AppSupervisor;

/// <summary>Configures one ordered action executed after AppSupervisor launches a helper.</summary>
public sealed class StartupMacroActionConfig
{
    /// <summary>Gets or sets the required action kind.</summary>
    public StartupMacroActionType? Type { get; set; }

    /// <summary>Gets or sets the nonblocking delay duration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DelayMilliseconds { get; set; }

    /// <summary>Gets or sets WinForms key names captured as one simultaneous chord.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Keys { get; set; }

    /// <summary>Gets or sets the target monitor device name; blank selects the primary monitor.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Monitor { get; set; }

    /// <summary>Gets or sets the monitor-work-area-relative horizontal coordinate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? X { get; set; }

    /// <summary>Gets or sets the monitor-work-area-relative vertical coordinate.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Y { get; set; }

    /// <summary>Gets or sets the requested normal window width.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; set; }

    /// <summary>Gets or sets the requested normal window height.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; set; }
}
