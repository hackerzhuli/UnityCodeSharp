Where are the assemblies?

They're in Library/ScriptAssemblies.

How do we know if an assembly is a user assembly?

Look at root of project and find .csproj files, extract files names, and that should match assembly names of user assemblies.

eg.

```
Assembly-CSharp.csproj
Hackerzhuli.Code.csproj
Assembly-CSharp.dll
Hackerzhuli.Code.dll
```
