using System.Collections;
using Mono.Debugging.Client;

namespace MonoDebugger;

public class DebugOptions
{
    public EvaluationOptions EvaluationOptions { get; set; }
    public bool ProjectAssembliesOnly { get; set; }
    public DebuggerSessionOptions Options { get; set; }
    public bool SearchMicrosoftSymbolServer { get; set; }
    public bool SearchNuGetSymbolServer { get; set; }
    public IEnumerable<string> SymbolSearchPaths { get; set; }
    public Dictionary<string, string> SourceCodeMappings { get; set; }
}