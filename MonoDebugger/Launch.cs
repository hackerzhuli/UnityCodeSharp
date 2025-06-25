using System.Net;
using System.Reflection;
using System.Text.Json;
using DotRush.Common.Extensions;
using DotRush.Common.MSBuild;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;

namespace MonoDebugger;

/// <summary>
/// Unity debug launch agent that handles debugging Unity applications.
/// </summary>
public class Launch {
    private readonly ExternalTypeResolver typeResolver;
    private SoftDebuggerStartInfo? startInformation;
    
    /// <summary>
    /// Gets the list of disposable actions to be called when disposing.
    /// </summary>
    protected List<Action> Disposables { get; init; }
    
    /// <summary>
    /// Gets the launch configuration.
    /// </summary>
    protected LaunchConfig Config { get; init; }

    /// <summary>
    /// Initializes a new instance of the UnityDebugLaunchAgent class.
    /// </summary>
    /// <param name="config">The launch configuration</param>
    public Launch(LaunchConfig config) {
        Disposables = new List<Action>();
        Config = config;
        typeResolver = new ExternalTypeResolver(config.TransportId);
    }

    /// <summary>
    /// Prepares the debug session for Unity debugging.
    /// </summary>
    /// <param name="debugSession">The debug session to prepare</param>
    public void Prepare(DebugSession debugSession) {
        var editorInstance = GetEditorInstance();
        debugSession.OnOutputDataReceived($"Attaching to Unity({editorInstance.ProcessId}) - {editorInstance.Version}");

        var port = Config.ProcessId != 0 ? Config.ProcessId : 56000 + (editorInstance.ProcessId % 1000);
        var applicationName = Path.GetFileName(Config.CurrentDirectory);
        startInformation = new SoftDebuggerStartInfo(new SoftDebuggerConnectArgs(applicationName, IPAddress.Loopback, port));
        SetAssemblies(startInformation);
    }
    
    /// <summary>
    /// Connects the debugger session to Unity.
    /// </summary>
    /// <param name="session">The debugger session to connect</param>
    public void Connect(SoftDebuggerSession session) {
        session.Run(startInformation, Config.DebuggerSessionOptions.Options);
        if (typeResolver.TryConnect()) {
            Disposables.Add(() => typeResolver.Dispose());
            session.TypeResolverHandler = typeResolver.Resolve;
        }
    }
    
    /// <summary>
    /// Gets the user assemblies for debugging.
    /// </summary>
    /// <returns>An enumerable of user assembly paths</returns>
    public IEnumerable<string> GetUserAssemblies() {
        if (Config.UserAssemblies != null && Config.UserAssemblies.Count > 0)
            return Config.UserAssemblies;

        var projectAssemblyPath = Path.Combine(Config.CurrentDirectory, "Library", "ScriptAssemblies", "Assembly-CSharp.dll");
        if (File.Exists(projectAssemblyPath))
            return new[] { projectAssemblyPath };

        var projectFilePaths = Directory.GetFiles(Config.CurrentDirectory, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projectFilePaths.Length == 1) {
            var project = MSBuildProjectsLoader.LoadProject(projectFilePaths[0]);
            var assemblyName = project?.GetAssemblyName();
            projectAssemblyPath = Path.Combine(Config.CurrentDirectory, "Library", "ScriptAssemblies", $"{assemblyName}.dll");
            if (File.Exists(projectAssemblyPath))
                return new[] { projectAssemblyPath };
        }

        Debug.LogError($"Could not find user assembly '{projectAssemblyPath}'. Specify 'userAssemblies' property in the launch configuration to override this behavior.");
        return Enumerable.Empty<string>();
    }

    /// <summary>
    /// Disposes all registered disposable resources.
    /// </summary>
    public void Dispose() {
        foreach (var disposable in Disposables) {
            try {
                disposable.Invoke();
                Debug.Log($"Disposing {disposable.Method.Name}");
            } catch (Exception ex) {
                Debug.LogError($"Error while disposing {disposable.Method.Name}: {ex.Message}");
            }
        }

        Disposables.Clear();
    }

    /// <summary>
    /// Sets up assemblies for the debugger start info.
    /// </summary>
    /// <param name="startInfo">The debugger start info to configure</param>
    protected void SetAssemblies(SoftDebuggerStartInfo startInfo) {
        var options = Config.DebuggerSessionOptions;
        var useSymbolServers = options.SearchMicrosoftSymbolServer || options.SearchNuGetSymbolServer;
        var assemblyPathMap = new Dictionary<string, string>();
        var assemblySymbolPathMap = new Dictionary<string, string>();
        var assemblyNames = new List<AssemblyName>();

        foreach (var assemblyPath in GetUserAssemblies()) {
            try {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (string.IsNullOrEmpty(assemblyName.FullName) || string.IsNullOrEmpty(assemblyName.Name)) {
                    Debug.Log($"Assembly '{assemblyPath}' has no name");
                    continue;
                }

                string? assemblySymbolsFilePath = SymbolServerExtensions.SearchSymbols(options.SymbolSearchPaths, assemblyPath);
                if (string.IsNullOrEmpty(assemblySymbolsFilePath) && options.SearchMicrosoftSymbolServer)
                    assemblySymbolsFilePath = SymbolServerExtensions.DownloadSourceSymbols(assemblyPath, assemblyName.Name, SymbolServerExtensions.MicrosoftSymbolServerAddress);
                if (string.IsNullOrEmpty(assemblySymbolsFilePath) && options.SearchNuGetSymbolServer)
                    assemblySymbolsFilePath = SymbolServerExtensions.DownloadSourceSymbols(assemblyPath, assemblyName.Name, SymbolServerExtensions.NuGetSymbolServerAddress);
                if (string.IsNullOrEmpty(assemblySymbolsFilePath))
                    Debug.Log($"No symbols found for '{assemblyPath}'");

                if (!string.IsNullOrEmpty(assemblySymbolsFilePath))
                    assemblySymbolPathMap.Add(assemblyName.FullName, assemblySymbolsFilePath);

                if (options.ProjectAssembliesOnly && SymbolServerExtensions.HasDebugSymbols(assemblyPath, useSymbolServers)) {
                    assemblyPathMap.TryAdd(assemblyName.FullName, assemblyPath);
                    assemblyNames.Add(assemblyName);
                    Debug.Log($"User assembly '{assemblyName.Name}' added");
                }
            } catch (Exception e) {
                Debug.LogError($"Error while processing assembly '{assemblyPath}'", e);
            }
        }

        startInfo.SymbolPathMap = assemblySymbolPathMap;
        startInfo.AssemblyPathMap = assemblyPathMap;
        startInfo.UserAssemblyNames = assemblyNames;
    }

    /// <summary>
    /// Gets the Unity editor instance information.
    /// </summary>
    /// <returns>The editor instance information</returns>
    private EditorInstance GetEditorInstance() {
        var editorInfo = Path.Combine(Config.CurrentDirectory, "Library", "EditorInstance.json");
        if (!File.Exists(editorInfo))
            throw ServerExtensions.GetProtocolException($"EditorInstance.json not found: '{editorInfo}'");

        var editorInstance = SafeExtensions.Invoke(() => JsonSerializer.Deserialize<EditorInstance>(File.ReadAllText(editorInfo)));
        if (editorInstance == null)
            throw ServerExtensions.GetProtocolException($"Failed to deserialize EditorInstance.json: '{editorInfo}'");

        return editorInstance;
    }
}