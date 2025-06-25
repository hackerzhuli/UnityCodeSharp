using System.Net;
using System.Reflection;
using System.Text.Json;
using DotRush.Common.MSBuild;
using Mono.Debugging.Soft;

namespace MonoDebugger;

/// <summary>
///     Handles launch debugging Unity apps.
/// </summary>
public class Launch
{
    private readonly ExternalTypeResolver _typeResolver;
    private SoftDebuggerStartInfo? _startInformation;

    /// <summary>
    ///     constructor
    /// </summary>
    /// <param name="config">The launch configuration</param>
    public Launch(LaunchConfig config)
    {
        Disposables = [];
        Config = config;
        _typeResolver = new ExternalTypeResolver(config.TransportId);
    }

    /// <summary>
    ///     Gets the list of disposable actions to be called when disposing.
    /// </summary>
    private List<Action> Disposables { get; init; }

    /// <summary>
    ///     Gets the launch configuration.
    /// </summary>
    private LaunchConfig Config { get; init; }

    /// <summary>
    ///     Prepares the debug session for Unity debugging.
    /// </summary>
    /// <param name="debugSession">The debug session to prepare</param>
    public void Prepare(DebugSession debugSession)
    {
        var editorInstance = GetEditorInstance();
        debugSession.OnOutputDataReceived($"Attaching to Unity({editorInstance.ProcessId}) - {editorInstance.Version}");

        var port = Config.ProcessId != 0 ? Config.ProcessId : 56000 + editorInstance.ProcessId % 1000;
        var appName = Path.GetFileName(Config.CurrentDirectory);
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
    /// <returns>An enumerable of user assembly paths</returns>
    private IEnumerable<string> GetUserAssemblies()
    {
        if (Config.UserAssemblies != null && Config.UserAssemblies.Count > 0)
            return Config.UserAssemblies;

        var projectAssemblyPath =
            Path.Combine(Config.CurrentDirectory, "Library", "ScriptAssemblies", "Assembly-CSharp.dll");
        if (File.Exists(projectAssemblyPath))
            return [projectAssemblyPath];

        var projectFilePaths = Directory.GetFiles(Config.CurrentDirectory, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projectFilePaths.Length == 1)
        {
            var project = MSBuildProjectsLoader.LoadProject(projectFilePaths[0]);
            var assemblyName = project?.GetAssemblyName();
            projectAssemblyPath = Path.Combine(Config.CurrentDirectory, "Library", "ScriptAssemblies",
                $"{assemblyName}.dll");
            if (File.Exists(projectAssemblyPath))
                return [projectAssemblyPath];
        }

        Debug.LogError(
            $"Could not find user assembly '{projectAssemblyPath}'. Specify 'userAssemblies' property in the launch configuration to override this behavior.");
        return [];
    }

    /// <summary>
    ///     Disposes all registered disposable resources.
    /// </summary>
    public void Dispose()
    {
        foreach (var disposable in Disposables)
            try
            {
                disposable.Invoke();
                Debug.Log($"Disposing {disposable.Method.Name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error while disposing {disposable.Method.Name}: {ex.Message}");
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
        var useSymbolServers = options.SearchMicrosoftSymbolServer || options.SearchNuGetSymbolServer;
        var assemblyPathMap = new Dictionary<string, string>();
        var assemblySymbolPathMap = new Dictionary<string, string>();
        var assemblyNames = new List<AssemblyName>();

        foreach (var assemblyPath in GetUserAssemblies())
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (string.IsNullOrEmpty(assemblyName.FullName) || string.IsNullOrEmpty(assemblyName.Name))
                {
                    Debug.Log($"Assembly '{assemblyPath}' has no name");
                    continue;
                }

                var assemblySymbolsFilePath =
                    symbolServer.SearchSymbols(assemblyPath, assemblyName.Name, true);

                if (string.IsNullOrEmpty(assemblySymbolsFilePath))
                    Debug.Log($"No symbols found for '{assemblyPath}'");

                if (!string.IsNullOrEmpty(assemblySymbolsFilePath))
                    assemblySymbolPathMap.Add(assemblyName.FullName, assemblySymbolsFilePath);

                if (options.ProjectAssembliesOnly &&
                    symbolServer.HasSymbols(assemblyPath, useSymbolServers))
                {
                    assemblyPathMap.TryAdd(assemblyName.FullName, assemblyPath);
                    assemblyNames.Add(assemblyName);
                    Debug.Log($"User assembly '{assemblyName.Name}' added");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Error while processing assembly '{assemblyPath}'", e);
            }

        startInfo.SymbolPathMap = assemblySymbolPathMap;
        startInfo.AssemblyPathMap = assemblyPathMap;
        startInfo.UserAssemblyNames = assemblyNames;
    }

    /// <summary>
    ///     Gets the Unity editor instance information.
    /// </summary>
    /// <returns>The editor instance information</returns>
    private EditorInstance GetEditorInstance()
    {
        // this is a file that Unity Editor will write to when it opens a project
        var editorInfo = Path.Combine(Config.CurrentDirectory, "Library", "EditorInstance.json");
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