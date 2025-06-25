namespace MonoDebugger;

/// <summary>
///     Static debug logging utility that writes to a file instead of console output.
/// </summary>
public static class Debug
{
    private static readonly string LogFilePath =
        Path.Combine(App.AppDataPath, "debug.log");

    private static readonly object LockObject = new();

    static Debug()
    {
        // Ensure the directory exists
        var directory = Path.GetDirectoryName(LogFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
    }

    /// <summary>
    ///     Logs an informational message to the debug file.
    /// </summary>
    /// <param name="message">The message to log</param>
    public static void Log(string message)
    {
        WriteToFile("INFO", message);
    }

    /// <summary>
    ///     Logs a warning message to the debug file.
    /// </summary>
    /// <param name="message">The warning message to log</param>
    public static void LogWarning(string message)
    {
        WriteToFile("WARN", message);
    }

    /// <summary>
    ///     Logs an error message to the debug file.
    /// </summary>
    /// <param name="message">The error message to log</param>
    public static void LogError(string message)
    {
        WriteToFile("ERROR", message);
    }

    /// <summary>
    ///     Logs an error message with exception details to the debug file.
    /// </summary>
    /// <param name="message">The error message to log</param>
    /// <param name="exception">The exception to log</param>
    public static void LogError(string message, Exception exception)
    {
        WriteToFile("ERROR", $"{message}: {exception}");
    }

    private static void WriteToFile(string level, string message)
    {
        try
        {
            lock (LockObject)
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogFilePath, logEntry);
            }
        }
        catch
        {
            // Silently ignore logging errors to prevent infinite loops
        }
    }
}