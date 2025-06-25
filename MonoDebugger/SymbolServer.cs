using System.Reflection.PortableExecutable;
using System.Text;
using Mono.Debugging.Client;

namespace MonoDebugger;

/// <summary>
///     Utilities for symbol server operations and source link functionality.
/// </summary>
/// <remarks>
///     Why are we downloading symbols?
///     All normal packages and project source code that is compiled by Unity and their assemblies and symbols are present
///     in Library/ScriptAssemblies
///     But prebuild assemblies some may come from NUGET don't have symbols and .Net assemblies or their symbols are not
///     present
/// </remarks>
public class SymbolServer : IDisposable
{
    /// <summary>
    ///     The Microsoft symbol server address for downloading debug symbols.
    /// </summary>
    private const string MicrosoftSymbolServerAddress = "https://msdl.microsoft.com/download/symbols";

    /// <summary>
    ///     The NuGet symbol server address for downloading debug symbols.
    /// </summary>
    private const string NuGetSymbolServerAddress = "https://symbols.nuget.org/download/symbols";

    private readonly HttpClient _httpClient;
    private readonly string _symbolsDirectory;
    private Action<string>? _eventLogger;

    public SymbolServer()
    {
        _httpClient = new HttpClient();
        _symbolsDirectory = Path.Combine(App.AppDataPath, "symbols");
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    /// <summary>
    ///     Sets the event logger for symbol server operations.
    /// </summary>
    /// <param name="logger">The action to use for logging events.</param>
    public void SetEventLogger(Action<string> logger)
    {
        _eventLogger = logger;
    }

    /// <summary>
    ///     Downloads a source file from the specified URI using source link.
    /// </summary>
    /// <param name="uri">The URI of the source file to download.</param>
    /// <returns>The content of the source file, or null if download failed.</returns>
    public string? DownloadSourceFile(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var sourceLinkUri))
        {
            DebuggerLoggingService.CustomLogger?.LogMessage($"Invalid source link '{uri}'");
            return null;
        }

        return GetFileContentAsync(uri).Result;
    }

    /// <summary>
    ///     Downloads debug symbols for an assembly from a symbol server.
    /// </summary>
    /// <param name="assemblyPath">The path to the assembly file.</param>
    /// <param name="assemblyName">The name of the assembly.</param>
    /// <param name="serverAddress">The symbol server address to download from.</param>
    /// <returns>The path to the downloaded PDB file, or null if download failed.</returns>
    private string? DownloadSourceSymbols(string assemblyPath, string assemblyName, string serverAddress)
    {
        var pdbData = GetPdbData(assemblyPath);
        if (pdbData == null)
            return null;

        var outputFilePath = Path.Combine(_symbolsDirectory, pdbData.Id + ".pdb");
        if (File.Exists(outputFilePath))
            return outputFilePath;

        var request = $"{serverAddress}/{assemblyName}.pdb/{pdbData.Id}FFFFFFFF/{assemblyName}.pdb";
        // var header = $"SymbolChecksum: {pdbData.Hash}";
        if (DownloadFileAsync(request, outputFilePath).Result)
        {
            _eventLogger?.Invoke($"Loaded symbols for '{assemblyName}'");
            return outputFilePath;
        }

        return null;
    }

    /// <summary>
    ///     Checks if debug symbols are available for an assembly, either locally or on symbol servers.
    /// </summary>
    /// <param name="assemblyPath">The path to the assembly file.</param>
    /// <param name="useSymbolServers">
    ///     Whether to check symbol servers in addition to local files(will not download symbol
    ///     files, just check the cache).
    /// </param>
    /// <returns>True if debug symbols are available, otherwise false.</returns>
    public bool HasSymbols(string assemblyPath, bool useSymbolServers)
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(pdbPath))
            return true;
        if (!useSymbolServers)
            return false;

        var pdbData = GetPdbData(assemblyPath);
        if (pdbData == null)
            return false;

        pdbPath = Path.Combine(_symbolsDirectory, pdbData.Id + ".pdb");
        return File.Exists(pdbPath);
    }

    /// <summary>
    ///     Searches for debug symbols in the specified search paths.
    /// </summary>
    /// <param name="assemblyPath">The path to the assembly file.</param>
    /// <param name="assemblyName">The name of the assembly.</param>
    /// <param name="useSymbolServers">Whether to search symbol servers in addition to local files(will download).</param>
    /// <returns>The path to the found PDB file, or null if not found.</returns>
    public string? SearchSymbols(string assemblyPath, string? assemblyName = null, bool useSymbolServers = false)
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(pdbPath))
            return pdbPath;
        if (!useSymbolServers) return null;

        if (assemblyName == null) assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

        var result = DownloadSourceSymbols(assemblyPath,
            assemblyName, MicrosoftSymbolServerAddress);
        if (result != null) return result;

        return DownloadSourceSymbols(assemblyPath,
            assemblyName, NuGetSymbolServerAddress);
    }

    private async Task<bool> DownloadFileAsync(string url, string outputFilePath)
    {
        try
        {
            // if (!string.IsNullOrEmpty(header)) {
            //     httpClient.DefaultRequestHeaders.Remove("SymbolChecksum");
            //     httpClient.DefaultRequestHeaders.Add("SymbolChecksum", header);
            // }
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return false;

            var directory = Path.GetDirectoryName(outputFilePath)!;
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using var content = response.Content;
            var data = await content.ReadAsByteArrayAsync();
            File.WriteAllBytes(outputFilePath, data);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<string?> GetFileContentAsync(string url)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            using var content = response.Content;
            var data = await content.ReadAsByteArrayAsync();
            return Encoding.Default.GetString(data);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static PdbData? GetPdbData(string assemblyPath)
    {
        try
        {
            using var peReader = new PEReader(File.OpenRead(assemblyPath));
            var codeViewEntries = peReader.ReadDebugDirectory()
                .Where(entry => entry.Type == DebugDirectoryEntryType.CodeView);
            var checkSumEntries = peReader.ReadDebugDirectory()
                .Where(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum);
            if (!codeViewEntries.Any() || !checkSumEntries.Any())
                return null;

            return new PdbData(
                peReader.ReadCodeViewDebugDirectoryData(codeViewEntries.First()),
                peReader.ReadPdbChecksumDebugDirectoryData(checkSumEntries.First())
            );
        }
        catch (Exception ex)
        {
            DebuggerLoggingService.CustomLogger?.LogError($"Error reading assembly '{assemblyPath}'", ex);
            return null;
        }
    }

    private class PdbData(CodeViewDebugDirectoryData codeView, PdbChecksumDebugDirectoryData checksum)
    {
        public string Id => codeView.Guid.ToString("N");

        public string Hash =>
            $"{checksum.AlgorithmName}:{Convert.ToHexString(checksum.Checksum.ToArray())}";
    }
}