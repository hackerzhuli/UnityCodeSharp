namespace MonoDebugger;

/// <summary>
/// Main program class for the MonoDebugger application.
/// </summary>
public class Program
{
    private static void Main(string[] args)
    {
        var debugSession =
            new DebugSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
        debugSession.Start();
    }
}