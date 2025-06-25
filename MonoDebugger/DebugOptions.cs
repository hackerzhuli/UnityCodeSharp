using Mono.Debugging.Client;
using Newtonsoft.Json;

namespace MonoDebugger;

/// <summary>
///     Debug options for configuring the debugger session and symbol resolution.
/// </summary>
public class DebugOptions
{
    /// <summary>
    ///     Gets or sets the evaluation options for expression evaluation.
    /// </summary>
    public EvaluationOptions EvaluationOptions
    {
        get => Options?.EvaluationOptions;
        set => Options.EvaluationOptions = value;
    }

    /// <summary>
    ///     Gets or sets a value indicating whether to debug only project assemblies.
    /// </summary>
    public bool ProjectAssembliesOnly
    {
        get => Options?.ProjectAssembliesOnly ?? false;
        set => Options.ProjectAssembliesOnly = value;
    }

    /// <summary>
    ///     Gets or sets the debugger session options. This property is not serialized to JSON.
    /// </summary>
    [JsonIgnore]
    public DebuggerSessionOptions Options { get; set; } = new();

    /// <summary>
    ///     Gets or sets a value indicating whether to step over properties and operators.
    /// </summary>
    public bool StepOverPropertiesAndOperators
    {
        get => Options?.StepOverPropertiesAndOperators ?? false;
        set => Options.StepOverPropertiesAndOperators = value;
    }

    /// <summary>
    ///     Gets or sets the automatic source link download behavior.
    /// </summary>
    public AutomaticSourceDownload AutomaticSourceLinkDownload
    {
        get => Options?.AutomaticSourceLinkDownload ?? AutomaticSourceDownload.Ask;
        set => Options.AutomaticSourceLinkDownload = value;
    }

    /// <summary>
    ///     Gets or sets a value indicating whether to debug subprocesses.
    /// </summary>
    public bool DebugSubprocesses
    {
        get => Options?.DebugSubprocesses ?? false;
        set => Options.DebugSubprocesses = value;
    }

    /// <summary>
    ///     Gets or sets the source code mappings.
    /// </summary>
    public Dictionary<string, string> SourceCodeMappings { get; set; } = new();

    /// <summary>
    ///     Gets or sets a value indicating whether to skip native transitions.
    /// </summary>
    public bool SkipNativeTransitions { get; set; }
}