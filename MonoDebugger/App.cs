namespace MonoDebugger;

public static class App
{
    public static string AppDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityCode");

    private static void Main(string[] args)
    {
        var debugSession =
            new DebugSession(Console.OpenStandardInput(), Console.OpenStandardOutput());
        debugSession.Start();
    }
}