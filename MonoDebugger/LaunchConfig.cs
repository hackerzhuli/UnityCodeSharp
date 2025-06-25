using DotRush.Common.Extensions;
using Mono.Debugging.Client;
using Newtonsoft.Json.Linq;

namespace MonoDebugger;

public class LaunchConfig {
    public string CurrentDirectory { get; init; }
    public int ProcessId { get; init; }
    public string? TransportId { get; init; }
    public DebugOptions DebuggerSessionOptions { get; init; }
    public Dictionary<string, string>? EnvironmentVariables { get; init; }
    public List<string>? UserAssemblies { get; init; }
    private bool SkipDebug { get; init; }

    public LaunchConfig(Dictionary<string, JToken> configurationProperties) {
        SkipDebug = configurationProperties.TryGetValue("skipDebug").ToValue<bool>();
        ProcessId = configurationProperties.TryGetValue("processId").ToValue<int>();
        TransportId = configurationProperties.TryGetValue("transportId").ToClass<string>();
        DebuggerSessionOptions = configurationProperties.TryGetValue("debuggerOptions")?.ToClass<DebugOptions>() ?? ServerExtensions.DefaultDebuggerOptions;
        EnvironmentVariables = configurationProperties.TryGetValue("env")?.ToClass<Dictionary<string, string>>();
        UserAssemblies = configurationProperties.TryGetValue("userAssemblies")?.ToClass<List<string>>();

        CurrentDirectory = configurationProperties.TryGetValue("cwd").ToClass<string>()?.ToPlatformPath().TrimPathEnd() ?? Environment.CurrentDirectory;
        if (Directory.Exists(CurrentDirectory) && CurrentDirectory != Environment.CurrentDirectory)
            Environment.CurrentDirectory = CurrentDirectory;
    }

    public Launch GetLaunchAgent() {
        return new Launch(this); //NoDebug?
    }
}