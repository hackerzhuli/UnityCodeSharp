# Project Use Case
This is C# project UnityCodeSharp mainly for a Mono Debugger for Unity used by a VS Code extension UnityCode, which is an extension for integrating VS Code with Unity game development.

.NET Version: 9.0
C# Version: 12.0

## Code Guidelines
- Remember to write xml docs for public members and the classes themselves
- Use var instead of explicit types if types can be infered
- Don't use #region

## How our debugger works
Core Dependencies:
- Mono.Debugging.Soft
- Microsoft.VisualStudio.Shared.VSCodeDebugProtocol

We use Mono.Debugging.Soft library to communicate with Unity instances through TCP connection, and Microsoft.VisualStudio.Shared.VSCodeDebugProtocol to communicate with VS Code through pipes(stdin/stdout). So that the debugger will work for VS Code.

