using DotRush.Common.Extensions;
using Newtonsoft.Json.Linq;

namespace MonoDebugger;

/// <summary>
/// Configuration class for Unity debug launch settings.
/// </summary>
public class LaunchConfig
{
    /// <summary>
    /// Initializes a new instance of the LaunchConfig class with the specified configuration properties.
    /// </summary>
    /// <param name="configurationProperties">Dictionary containing configuration properties from the launch request.</param>
    public LaunchConfig(Dictionary<string, JToken> configurationProperties)
    {
        SkipDebug = configurationProperties.TryGetValue("skipDebug").ToValue<bool>();
        ProcessId = configurationProperties.TryGetValue("processId").ToValue<int>();
        TransportId = configurationProperties.TryGetValue("transportId").ToClass<string>();
        DebuggerSessionOptions = configurationProperties.TryGetValue("debuggerOptions")?.ToClass<DebugOptions>() ??
                                 ServerExtensions.DefaultDebuggerOptions;
        EnvironmentVariables = configurationProperties.TryGetValue("env")?.ToClass<Dictionary<string, string>>();
        UserAssemblies = configurationProperties.TryGetValue("userAssemblies")?.ToClass<List<string>>();

        CurrentDirectory =
            configurationProperties.TryGetValue("cwd").ToClass<string>()?.ToPlatformPath().TrimPathEnd() ??
            Environment.CurrentDirectory;
        if (Directory.Exists(CurrentDirectory) && CurrentDirectory != Environment.CurrentDirectory)
            Environment.CurrentDirectory = CurrentDirectory;
    }

    /// <summary>
    /// Gets the current working directory for the debug session.
    /// </summary>
    public string CurrentDirectory { get; init; }
    
    /// <summary>
    /// Gets the process ID of the Unity Editor instance to attach to.
    /// </summary>
    public int ProcessId { get; init; }
    
    /// <summary>
    /// Gets the transport ID for external type resolver communication.
    /// </summary>
    public string? TransportId { get; init; }
    
    /// <summary>
    /// Gets the debugger session options and settings.
    /// </summary>
    public DebugOptions DebuggerSessionOptions { get; init; }
    
    /// <summary>
    /// Gets the environment variables to set for the debug session.
    /// </summary>
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
    
    /// <summary>
    /// Gets the list of user assemblies to include in debugging.
    /// </summary>
    public List<string>? UserAssemblies { get; init; }
    
    private bool SkipDebug { get; init; }

    /// <summary>
    /// Creates and returns a new Launch agent configured with this launch configuration.
    /// </summary>
    /// <returns>A new Launch instance configured for Unity debugging.</returns>
    public Launch GetLaunchAgent()
    {
        return new Launch(this); //NoDebug?
    }
}