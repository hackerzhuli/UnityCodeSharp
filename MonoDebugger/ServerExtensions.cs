using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;
using Newtonsoft.Json.Linq;
using NewtonConverter = Newtonsoft.Json.JsonConvert;
using DebugProtocol = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace MonoDebugger;

/// <summary>
/// Extension methods and utilities for debug server operations.
/// </summary>
public static class ServerExtensions
{
    /// <summary>
    /// Gets the default debugger options with predefined evaluation settings for Unity debugging.
    /// </summary>
    public static DebugOptions DefaultDebuggerOptions { get; } = new()
    {
        EvaluationOptions = new EvaluationOptions
        {
            EvaluationTimeout = 1000,
            MemberEvaluationTimeout = 5000,
            UseExternalTypeResolver = true,
            AllowMethodEvaluation = true,
            GroupPrivateMembers = true,
            GroupStaticMembers = true,
            AllowToStringCalls = true,
            AllowTargetInvoke = true,
            EllipsizeStrings = true,
            EllipsizedLength = 260,
            CurrentExceptionTag = "$exception",
            IntegerDisplayFormat = IntegerDisplayFormat.Decimal,
            StackFrameFormat = new StackFrameFormat()
        },
        ProjectAssembliesOnly = true
    };

    /// <summary>
    /// Gets the JSON serializer options configured for debug protocol communication.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Creates a protocol exception with the specified message.
    /// </summary>
    /// <param name="message">The error message for the exception.</param>
    /// <returns>A new ProtocolException instance.</returns>
    public static ProtocolException GetProtocolException(string message)
    {
        return new ProtocolException(message, 0, message);
    }

    /// <summary>
    /// Gets a unique source reference identifier for a stack frame.
    /// </summary>
    /// <param name="frame">The stack frame to get the reference for.</param>
    /// <returns>A hash-based source reference ID.</returns>
    public static int GetSourceReference(this StackFrame frame)
    {
        var key = frame.SourceLocation.MethodName ?? "null";
        if (!string.IsNullOrEmpty(frame.SourceLocation.FileName))
        {
            key = frame.SourceLocation.FileName;
            if (frame.SourceLocation.FileName.Contains(".g.cs", StringComparison.OrdinalIgnoreCase))
                key = $"{frame.SourceLocation.FileName}:{frame.SourceLocation.Line}";
        }

        return Math.Abs(key.GetHashCode());
    }

    /// <summary>
    /// Trims and normalizes an expression from evaluate arguments.
    /// </summary>
    /// <param name="args">The evaluate arguments containing the expression.</param>
    /// <returns>The trimmed and normalized expression string.</returns>
    public static string? TrimExpression(this DebugProtocol.EvaluateArguments args)
    {
        return args.Expression?.TrimEnd(';')?.Replace("?.", ".");
    }

    /// <summary>
    /// Executes a function safely, handling exceptions and converting them to protocol exceptions.
    /// </summary>
    /// <typeparam name="T">The return type of the handler function.</typeparam>
    /// <param name="handler">The function to execute safely.</param>
    /// <param name="finalizer">Optional action to execute in case of an exception.</param>
    /// <returns>The result of the handler function.</returns>
    /// <exception cref="ProtocolException">Thrown when an exception occurs during execution.</exception>
    public static T DoSafe<T>(Func<T> handler, Action? finalizer = null)
    {
        try
        {
            return handler.Invoke();
        }
        catch (Exception ex)
        {
            finalizer?.Invoke();
            if (ex is ProtocolException)
                throw;
            DebuggerLoggingService.CustomLogger?.LogError($"[Handled] {ex.Message}", ex);
            throw GetProtocolException(ex.Message);
        }
    }

    /// <summary>
    /// Safely tries to get a value from a dictionary, returning null if the key doesn't exist.
    /// </summary>
    /// <param name="dictionary">The dictionary to search in.</param>
    /// <param name="key">The key to look for.</param>
    /// <returns>The JToken value if found, otherwise null.</returns>
    public static JToken? TryGetValue(this Dictionary<string, JToken> dictionary, string key)
    {
        if (dictionary.TryGetValue(key, out var value))
            return value;
        return null;
    }

    /// <summary>
    /// Converts a JToken to a reference type using JSON deserialization.
    /// </summary>
    /// <typeparam name="T">The target reference type.</typeparam>
    /// <param name="jtoken">The JToken to convert.</param>
    /// <returns>The deserialized object or null if conversion fails.</returns>
    public static T? ToClass<T>(this JToken? jtoken) where T : class
    {
        if (jtoken == null)
            return default;

        var json = NewtonConverter.SerializeObject(jtoken);
        if (string.IsNullOrEmpty(json))
            return default;

        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    /// <summary>
    /// Converts a JToken to a value type using JSON deserialization.
    /// </summary>
    /// <typeparam name="T">The target value type.</typeparam>
    /// <param name="jtoken">The JToken to convert.</param>
    /// <returns>The deserialized value or default if conversion fails.</returns>
    public static T ToValue<T>(this JToken? jtoken) where T : struct
    {
        if (jtoken == null)
            return default;

        var json = NewtonConverter.SerializeObject(jtoken);
        if (string.IsNullOrEmpty(json))
            return default;

        return JsonSerializer.Deserialize<T>(json, SerializerOptions);
    }

    /// <summary>
    /// Converts a Mono debugger CompletionItem to a debug protocol CompletionItem.
    /// </summary>
    /// <param name="item">The completion item to convert.</param>
    /// <returns>A debug protocol completion item.</returns>
    public static DebugProtocol.CompletionItem ToCompletionItem(this CompletionItem item)
    {
        return new DebugProtocol.CompletionItem
        {
            Type = item.Flags.ToCompletionItemType(),
            SortText = item.Name,
            Label = item.Name
        };
    }

    private static DebugProtocol.CompletionItemType ToCompletionItemType(this ObjectValueFlags flags)
    {
        if (flags.HasFlag(ObjectValueFlags.Method))
            return DebugProtocol.CompletionItemType.Method;
        if (flags.HasFlag(ObjectValueFlags.Field))
            return DebugProtocol.CompletionItemType.Field;
        if (flags.HasFlag(ObjectValueFlags.Property))
            return DebugProtocol.CompletionItemType.Property;
        if (flags.HasFlag(ObjectValueFlags.Namespace))
            return DebugProtocol.CompletionItemType.Module;
        if (flags.HasFlag(ObjectValueFlags.Type))
            return DebugProtocol.CompletionItemType.Class;
        if (flags.HasFlag(ObjectValueFlags.Variable))
            return DebugProtocol.CompletionItemType.Variable;

        return DebugProtocol.CompletionItemType.Text;
    }

    /// <summary>
    /// Converts a Mono debugger Breakpoint to a debug protocol Breakpoint.
    /// </summary>
    /// <param name="breakpoint">The breakpoint to convert.</param>
    /// <param name="session">The debugger session for status information.</param>
    /// <returns>A debug protocol breakpoint with verification status.</returns>
    public static DebugProtocol.Breakpoint ToBreakpoint(this Breakpoint breakpoint, SoftDebuggerSession session)
    {
        return new DebugProtocol.Breakpoint
        {
            Id = breakpoint.GetHashCode(),
            Verified = breakpoint.GetStatus(session) == BreakEventStatus.Bound,
            Message = breakpoint.GetStatusMessage(session),
            Line = breakpoint.Line,
            Column = breakpoint.Column
        };
    }

    /// <summary>
    /// Converts a Mono debugger Assembly to a debug protocol Module.
    /// </summary>
    /// <param name="assembly">The assembly to convert.</param>
    /// <returns>A debug protocol module with assembly information.</returns>
    public static DebugProtocol.Module ToModule(this Assembly assembly)
    {
        return new DebugProtocol.Module
        {
            Id = assembly.Name.GetHashCode(),
            Name = assembly.Name,
            Path = assembly.Path,
            IsOptimized = assembly.Optimized,
            IsUserCode = assembly.UserCode,
            Version = assembly.Version,
            SymbolFilePath = assembly.SymbolFile,
            DateTimeStamp = assembly.TimeStamp,
            AddressRange = assembly.Address,
            SymbolStatus = assembly.SymbolStatus,
            VsAppDomain = assembly.AppDomain
        };
    }

    /// <summary>
    /// Converts a SourceLink to debug protocol VSSourceLinkInfo.
    /// </summary>
    /// <param name="sourceLink">The source link to convert.</param>
    /// <returns>A debug protocol source link info object.</returns>
    public static DebugProtocol.VSSourceLinkInfo ToSourceLinkInfo(this SourceLink sourceLink)
    {
        return new DebugProtocol.VSSourceLinkInfo
        {
            Url = sourceLink?.Uri,
            RelativeFilePath = sourceLink?.RelativeFilePath
        };
    }

    /// <summary>
    /// Converts a debug protocol Variable to a SetVariableResponse.
    /// </summary>
    /// <param name="variable">The variable to convert.</param>
    /// <returns>A set variable response with the variable's properties.</returns>
    public static DebugProtocol.SetVariableResponse ToSetVariableResponse(this DebugProtocol.Variable variable)
    {
        return new DebugProtocol.SetVariableResponse
        {
            Value = variable.Value,
            Type = variable.Type,
            VariablesReference = variable.VariablesReference,
            NamedVariables = variable.NamedVariables,
            IndexedVariables = variable.IndexedVariables
        };
    }

    /// <summary>
    /// Creates a goto targets response for jump-to-cursor functionality.
    /// </summary>
    /// <param name="args">The goto targets arguments containing line and column information.</param>
    /// <param name="id">The target ID for the jump operation.</param>
    /// <returns>A goto targets response with a single jump-to-cursor target.</returns>
    public static DebugProtocol.GotoTargetsResponse ToJumpToCursorTarget(this DebugProtocol.GotoTargetsArguments args,
        int id)
    {
        return new DebugProtocol.GotoTargetsResponse
        {
            Targets = new List<DebugProtocol.GotoTarget>
            {
                new()
                {
                    Id = id,
                    Label = "Jump to cursor",
                    Line = args.Line,
                    Column = args.Column,
                    EndLine = 0,
                    EndColumn = 0
                }
            }
        };
    }

    /// <summary>
    /// Converts an ExceptionInfo to a debug protocol ExceptionInfoResponse.
    /// </summary>
    /// <param name="exeption">The exception information to convert.</param>
    /// <returns>A debug protocol exception info response with details and stack trace.</returns>
    public static DebugProtocol.ExceptionInfoResponse ToExceptionInfoResponse(this ExceptionInfo exeption)
    {
        return new DebugProtocol.ExceptionInfoResponse(exeption.Type, DebugProtocol.ExceptionBreakMode.Always)
        {
            Description = exeption.Message,
            Details = exeption.ToExceptionDetails()
        };
    }

    private static DebugProtocol.ExceptionDetails? ToExceptionDetails(this ExceptionInfo? exception)
    {
        if (exception == null)
            return null;

        var innerExceptions = new List<ExceptionInfo>();
        if (exception.InnerException != null)
            innerExceptions.Add(exception.InnerException);

        var _innerExceptions = exception.InnerExceptions;
        if (_innerExceptions != null)
            innerExceptions.AddRange(_innerExceptions.Where(it => it != null));

        return new DebugProtocol.ExceptionDetails
        {
            FullTypeName = exception.Type,
            Message = exception.Message,
            InnerException = innerExceptions.Select(it => it.ToExceptionDetails()).ToList(),
            StackTrace = string.Join('\n',
                exception.StackTrace?.Select(it => it.ToStackTraceLine()) ?? Array.Empty<string>())
        };
    }

    private static string ToStackTraceLine(this ExceptionStackFrame? frame)
    {
        var sb = new StringBuilder();
        if (frame?.DisplayText == null)
            return "<unknown>";

        sb.Append("    ");
        if (!frame.DisplayText.StartsWith("at "))
            sb.Append("at ");

        sb.Append(frame.DisplayText);
        if (!string.IsNullOrEmpty(frame.File))
            sb.Append($" in {frame.File}:line {frame.Line}");

        return sb.ToString();
    }
}