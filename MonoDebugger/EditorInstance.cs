using System.Text.Json.Serialization;

namespace MonoDebugger;

/// <summary>
/// Represents a Unity Editor instance with its process information and paths.
/// </summary>
public class EditorInstance
{
    /// <summary>
    /// Gets or sets the process ID of the Unity Editor instance.
    /// </summary>
    [JsonPropertyName("process_id")] public int ProcessId { get; set; }
    
    /// <summary>
    /// Gets or sets the version of the Unity Editor instance.
    /// </summary>
    [JsonPropertyName("version")] public string? Version { get; set; }
    
    /// <summary>
    /// Gets or sets the application path of the Unity Editor instance.
    /// </summary>
    [JsonPropertyName("app_path")] public string? AppPath { get; set; }

    /// <summary>
    /// Gets or sets the application contents path of the Unity Editor instance.
    /// </summary>
    [JsonPropertyName("app_contents_path")]
    public string? AppContentsPath { get; set; }
}