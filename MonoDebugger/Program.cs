namespace MonoDebugger;

/// <summary>
/// Main program class for the MonoDebugger application.
/// </summary>
public class Program
{
    private static void Main(string[] args)
    {
        // TODO: Need to specify options
        var debugSession =
            new DebugSession(Console.OpenStandardInput(), Console.OpenStandardOutput(), new DebugOptions());
        debugSession.Start();
    }
}