using System.Net;
using System.Reflection;
using System.Text.Json;
using Mono.Debugging.Soft;

namespace MonoDebugger;

/// <summary>
///     Handles launch debugging Unity apps.
/// </summary>
/// <remarks>
///     constructor
/// </remarks>
/// <param name="config">The launch configuration</param>
public class Launch(LaunchConfig config)
{
    private readonly ExternalTypeResolver _typeResolver = new ExternalTypeResolver(config.TransportId);
    private SoftDebuggerStartInfo? _startInformation;

    /// <summary>
    ///     Gets the list of disposable actions to be called when disposing.
    /// </summary>
    private List<Action> Disposables { get; init; } = [];

    /// <summary>
    ///     Gets the launch configuration.
    /// </summary>
    private LaunchConfig Config { get; init; } = config;

    /// <summary>
    ///     Initializes the debug session for Unity debugging.
    /// </summary>
    /// <param name="debugSession">The debug session to prepare</param>
    public void Init(DebugSession debugSession)
    {
        var editorInstance = GetEditorInstance();
        debugSession.OnOutputDataReceived($"Attaching to Unity({editorInstance.ProcessId}) - {editorInstance.Version}");
        debugSession.Options = Config.DebuggerSessionOptions;
        var port = Config.ProcessId != 0 ? Config.ProcessId : 56000 + editorInstance.ProcessId % 1000;
        var appName = Path.GetFileName(Config.ProjectPath);
        _startInformation =
            new SoftDebuggerStartInfo(new SoftDebuggerConnectArgs(appName, IPAddress.Loopback, port));
        SetAssemblies(_startInformation, debugSession.SymbolServer);
    }

    /// <summary>
    ///     Connects the debugger session to Unity.
    /// </summary>
    /// <param name="session">The debugger session to connect</param>
    public void Connect(SoftDebuggerSession session)
    {
        session.Run(_startInformation, Config.DebuggerSessionOptions.Options);
        if (_typeResolver.TryConnect())
        {
            Disposables.Add(() => _typeResolver.Dispose());
            session.TypeResolverHandler = _typeResolver.Resolve;
        }
    }

    /// <summary>
    ///     Gets the user assemblies for debugging.
    /// </summary>
    /// <returns>An list of user assembly paths</returns>
    private string[] GetAssemblies()
    {
        var scriptAssembliesPath = Path.Combine(Config.ProjectPath, "Library", "ScriptAssemblies");
        if (!Directory.Exists(scriptAssembliesPath))
        {
            Debug.LogError($"ScriptAssemblies directory not found at '{scriptAssembliesPath}'. ");
            return [];
        }

        return Directory.GetFiles(scriptAssembliesPath, "*.dll", SearchOption.TopDirectoryOnly).ToArray();
    }

    /// <summary>
    ///     Disposes all registered disposable resources.
    /// </summary>
    public void Dispose()
    {
        foreach (var disposable in Disposables)
        {
            try
            {
                disposable.Invoke();
                Debug.Log($"Disposing {disposable.Method.Name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error while disposing {disposable.Method.Name}: {ex.Message}");
            }
        }
        Disposables.Clear();
    }

    /// <summary>
    ///     Sets up assemblies for the debugger start info.
    /// </summary>
    /// <param name="startInfo">The debugger start info to configure</param>
    /// <param name="symbolServer">The symbol server instance to use</param>
    private void SetAssemblies(SoftDebuggerStartInfo startInfo, SymbolServer symbolServer)
    {
        var options = Config.DebuggerSessionOptions;
        var assemblyPathMap = new Dictionary<string, string>();
        var assemblySymbolPathMap = new Dictionary<string, string>();
        var userAssemblyNames = new List<AssemblyName>();
        
        try
        {
            foreach (var assemblyPath in GetAssemblies())
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (string.IsNullOrEmpty(assemblyName.FullName) || string.IsNullOrEmpty(assemblyName.Name))
                {
                    Debug.Log($"Assembly '{assemblyPath}' has no name");
                    continue;
                }

                var assemblySymbolsFilePath =
                    symbolServer.SearchSymbols(assemblyPath);

                if (string.IsNullOrEmpty(assemblySymbolsFilePath))
                {
                    Debug.Log($"No symbols found for '{assemblyPath}'");
                    continue;
                }

                assemblySymbolPathMap.TryAdd(assemblyName.FullName, assemblySymbolsFilePath);
                assemblyPathMap.TryAdd(assemblyName.FullName, assemblyPath);
                
                // Check if this is a user assembly by looking for corresponding .csproj file
                var csprojPath = Path.Combine(Config.ProjectPath, $"{assemblyName.Name}.csproj");
                var isUserAssembly = File.Exists(csprojPath);
                
                if (isUserAssembly)
                {
                    userAssemblyNames.Add(assemblyName);
                    Debug.Log($"User assembly '{assemblyName.Name}' added (found .csproj)");
                }
                else
                {
                    Debug.Log($"System assembly '{assemblyName.Name}' added");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error while processing assemblies", e);
        }
        
        Debug.Log($"Total assemblies processed: {assemblyPathMap.Count}, User assemblies: {userAssemblyNames.Count}");
        startInfo.SymbolPathMap = assemblySymbolPathMap;
        startInfo.AssemblyPathMap = assemblyPathMap;
        startInfo.UserAssemblyNames = userAssemblyNames;
    }

    /// <summary>
    ///     Gets the Unity editor instance information.
    /// </summary>
    /// <returns>The editor instance information</returns>
    private EditorInstance GetEditorInstance()
    {
        // this is a file that Unity Editor will write to when it opens a project
        var editorInfo = Path.Combine(Config.ProjectPath, "Library", "EditorInstance.json");
        if (!File.Exists(editorInfo))
            throw ServerExtensions.GetProtocolException($"EditorInstance.json not found: '{editorInfo}'");

        EditorInstance? editorInstance;
        try
        {
            editorInstance = JsonSerializer.Deserialize<EditorInstance>(File.ReadAllText(editorInfo));
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to read EditorInstance.json: '{editorInfo}' - {e.Message}");
            throw ServerExtensions.GetProtocolException($"Failed to read EditorInstance.json: '{editorInfo}'");
        }

        return editorInstance;
    }
}