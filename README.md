# Unity Code Sharp

The C# components of Unity Code, the VS Code extension for Unity. For now it's just MonoDebugger.

## Build
Make sure you have installed .NET SDK 9 or higher. Navigate to solution directory in command line, and run the following command(replace win-x64 with the platform you wish to publish for).

```bash
dotnet publish -r win-x64
```

And that's it. Find the executable in the output publish directory that is shown in command line output. MonoDebugger is a single file executable, copy it to where you need it. Also there is `Microsoft.Unity.Analyzers.dll`, copy that to where you need it.
