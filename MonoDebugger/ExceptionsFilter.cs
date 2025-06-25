using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace MonoDebugger;

/// <summary>
/// Provides predefined exception breakpoint filters for debugging.
/// </summary>
public static class ExceptionsFilter
{
    /// <summary>
    /// Gets an exception breakpoint filter that breaks on all exceptions.
    /// Supports conditional filtering with comma-separated exception types.
    /// </summary>
    public static ExceptionBreakpointsFilter AllExceptions => new()
    {
        Filter = "all",
        Label = "All Exceptions",
        Description = "Break when an exception is thrown.",
        ConditionDescription =
            "Comma-separated list of exception types to break on, or if the list starts with '!', a list of exception types to ignore.",
        SupportsCondition = true
    };
}