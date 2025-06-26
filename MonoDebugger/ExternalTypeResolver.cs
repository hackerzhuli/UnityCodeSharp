using System.IO.Pipes;
using Mono.Debugging.Client;
using StreamJsonRpc;

namespace MonoDebugger;

/// <summary>
/// Provides external type resolution capabilities through JSON-RPC communication over named pipes.
/// </summary>
public class ExternalTypeResolver : IDisposable
{
    private readonly NamedPipeClientStream? _transportStream;
    private JsonRpc? _rpcServer;

    /// <summary>
    /// Initializes a new instance of the ExternalTypeResolver class.
    /// </summary>
    /// <param name="transportId">The transport ID for the named pipe connection. If null or empty, no connection will be established.</param>
    public ExternalTypeResolver(string? transportId)
    {
        if (!string.IsNullOrEmpty(transportId))
        {
            _transportStream =
                new NamedPipeClientStream(".", transportId, PipeDirection.InOut, PipeOptions.Asynchronous);
        }
    }

    /// <summary>
    /// Disposes the external type resolver and releases all resources.
    /// </summary>
    public void Dispose()
    {
        _rpcServer?.Dispose();
        _transportStream?.Dispose();
    }

    /// <summary>
    /// Attempts to connect to the external type resolver service.
    /// </summary>
    /// <param name="timeoutMs">The connection timeout in milliseconds. Default is 5000ms.</param>
    /// <returns>True if the connection was successful; otherwise, false.</returns>
    public bool TryConnect(int timeoutMs = 5000)
    {
        if (_transportStream == null)
            return false;

        try
        {
            _transportStream.Connect(timeoutMs);
            _rpcServer = JsonRpc.Attach(_transportStream);
            DebuggerLoggingService.CustomLogger?.LogMessage("Debugger connected to external type resolver");
            Debug.Log("Debugger connected to external type resolver");
        }
        catch (Exception e)
        {
            DebuggerLoggingService.CustomLogger?.LogMessage($"Failed to connect to external type resolver: {e}");
            Debug.Log("Failed to connect to external type resolver");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves a type identifier at the specified source location.
    /// </summary>
    /// <param name="identifierName">The identifier name to resolve.</param>
    /// <param name="location">The source location where the identifier is used.</param>
    /// <returns>The resolved type name, or null if resolution failed.</returns>
    public string? Resolve(string identifierName, SourceLocation location)
    {
        Debug.Log($"trying to resolve, identifier name: {identifierName} location: {location.FileName}:{location.Line}");
        try
        {
            var r = _rpcServer?.InvokeAsync<string>("HandleResolveType", identifierName, location)?.Result;
            Debug.Log($"identifier name: {identifierName} location: {location.FileName}:{location.Line}, resolved to type: {r}");
            return r;
        }
        catch (Exception e)
        {
            Debug.Log($"Failed to resolve type : {e.Message}");
            throw;
        }
    }
}