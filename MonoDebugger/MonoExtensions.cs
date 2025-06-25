using System.Text;
using DotRush.Common.Extensions;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;

namespace MonoDebugger;

/// <summary>
/// Extension methods for Mono debugging types to provide additional functionality.
/// </summary>
public static class MonoExtensions
{
    /// <summary>
    /// Converts a thread name and ID to a display-friendly thread name.
    /// </summary>
    /// <param name="threadName">The original thread name.</param>
    /// <param name="threadId">The thread ID.</param>
    /// <returns>A formatted thread name, defaulting to "Main Thread" for thread 1 or "Thread #ID" for others.</returns>
    public static string ToThreadName(this string threadName, int threadId)
    {
        if (!string.IsNullOrEmpty(threadName))
            return threadName;
        if (threadId == 1)
            return "Main Thread";
        return $"Thread #{threadId}";
    }

    /// <summary>
    /// Converts an ObjectValue to a display-friendly string representation.
    /// </summary>
    /// <param name="value">The ObjectValue to convert.</param>
    /// <returns>A formatted display value with braces removed and newlines replaced with spaces.</returns>
    public static string ToDisplayValue(this ObjectValue value)
    {
        var dv = value.DisplayValue ?? "<error getting value>";
        if (dv.Length > 1 && dv[0] == '{' && dv[dv.Length - 1] == '}')
            dv = dv.Substring(1, dv.Length - 2).Replace(Environment.NewLine, " ");
        return dv;
    }

    /// <summary>
    /// Safely retrieves a stack frame from a backtrace, handling exceptions gracefully.
    /// </summary>
    /// <param name="bt">The backtrace to get the frame from.</param>
    /// <param name="n">The frame index to retrieve.</param>
    /// <returns>The stack frame at the specified index, or null if an error occurred.</returns>
    public static StackFrame? GetFrameSafe(this Backtrace bt, int n)
    {
        try
        {
            return bt.GetFrame(n);
        }
        catch (Exception e)
        {
            DebuggerLoggingService.CustomLogger?.LogError($"Error while getting frame [{n}]", e);
            return null;
        }
    }

    /// <summary>
    /// Gets the disassembled IL code for a stack frame.
    /// </summary>
    /// <param name="frame">The stack frame to disassemble.</param>
    /// <returns>A formatted string containing the IL assembly code with addresses and source line numbers.</returns>
    public static string GetAssemblyCode(this StackFrame frame)
    {
        var assemblyLines = frame.Disassemble(-1, -1);
        var sb = new StringBuilder();
        foreach (var line in assemblyLines)
            sb.AppendLine($"({line.SourceLine}) IL_{line.Address:0000}: {line.Code}");

        return sb.ToString();
    }

    public static bool HasNullValue(this ObjectValue objectValue)
    {
        return objectValue.Value == "(null)";
    }

    public static string ResolveValue(this ObjectValue variable, string value)
    {
        var fullName = variable.TypeName;
        if (string.IsNullOrEmpty(fullName))
            return value;

        var shortName = fullName.Split('.').Last();
        if (!value.StartsWith($"new {shortName}"))
            return value;

        return value.Replace($"new {shortName}", $"new {fullName}");
    }

    public static ThreadInfo? FindThread(this SoftDebuggerSession session, long id)
    {
        var process = session.GetProcesses().FirstOrDefault();
        if (process == null)
            return null;

        return process.GetThreads().FirstOrDefault(it => it.Id == id);
    }

    public static ExceptionInfo? FindException(this SoftDebuggerSession session, long id)
    {
        var thread = session.FindThread(id);
        if (thread == null)
            return null;

        for (var i = 0; i < thread.Backtrace.FrameCount; i++)
        {
            var frame = thread.Backtrace.GetFrameSafe(i);
            var ex = frame?.GetException();
            if (ex != null)
                return ex;
        }

        return null;
    }

    public static string? RemapSourceLocation(this DebugSession session, SourceLocation location)
    {
        if (string.IsNullOrEmpty(location.FileName))
            return null;

        foreach (var remap in session.Options.SourceCodeMappings)
        {
            var filePath = location.FileName.ToPlatformPath();
            var key = remap.Key.ToPlatformPath();
            var value = remap.Value.ToPlatformPath();
            if (filePath.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return filePath.Replace(key, value);
        }

        return location.FileName;
    }
}