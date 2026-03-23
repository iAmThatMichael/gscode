using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using Serilog;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Microsoft.Extensions.Logging;
using System.IO; // added
using System.Linq; // added
using System.Threading; // added
using System.Diagnostics; // added
using OmniSharp.Extensions.LanguageServer.Protocol.Server; // added
using OmniSharp.Extensions.LanguageServer.Protocol.Document; // added for PublishDiagnostics extension

namespace GSCode.NET.LSP;

public class ScriptCache
{
    private ConcurrentDictionary<DocumentUri, StringBuilder> Scripts { get; } = new();

    public string AddToCache(TextDocumentItem document)
    {
        DocumentUri documentUri = document.Uri;
        Scripts[documentUri] = new(document.Text);

        return document.Text;
    }

    public string UpdateCache(TextDocumentIdentifier document, IEnumerable<TextDocumentContentChangeEvent> changes)
    {
        DocumentUri documentUri = document.Uri;
        StringBuilder cachedVersion = Scripts[documentUri];

        foreach (TextDocumentContentChangeEvent change in changes)
        {
            // If no range is specified then this is an outright replacement of the entire document.
            // This happens when client sends full sync (fallback) or on certain operations.
            if (change.Range == null)
            {
                cachedVersion = new(change.Text);
                Scripts[documentUri] = cachedVersion;
                Scripts[documentUri] = cachedVersion;
                continue;
            }

            Position start = change.Range.Start;
            Position end = change.Range.End;

            // Apply incremental change: replace text in the specified range
            string cachedString = cachedVersion.ToString();
            int startLineBase = GetBaseCharOfLine(cachedString, start.Line);
            int endLineBase = GetBaseCharOfLine(cachedString, end.Line);

            // Validate positions
            if (startLineBase == -1)
            {
                // Start line doesn't exist - shouldn't happen, but fall back to appending
                Log.Warning("Incremental update: start line {Line} not found in document {Uri}", start.Line, documentUri);
                cachedVersion.Append(change.Text);
                continue;
            }

            int startPosition = startLineBase + start.Character;

            // Handle edge case: end position beyond buffer or end line doesn't exist
            if (endLineBase == -1 || startPosition > cachedVersion.Length)
            {
                // Clamp to end of document
                if (startPosition > cachedVersion.Length)
                {
                    startPosition = cachedVersion.Length;
                }
                cachedVersion.Remove(startPosition, cachedVersion.Length - startPosition);
                cachedVersion.Append(change.Text);
                continue;
            }

            int endPosition = endLineBase + end.Character;
            if (endPosition > cachedVersion.Length)
            {
                endPosition = cachedVersion.Length;
            }

            // Ensure valid range
            if (startPosition > endPosition)
            {
                Log.Warning("Incremental update: invalid range [{Start},{End}) in document {Uri}", startPosition, endPosition, documentUri);
                continue;
            }

            // Standard incremental update: remove old text and insert new
            cachedVersion.Remove(startPosition, endPosition - startPosition);
            cachedVersion.Insert(startPosition, change.Text);
        }

        // Ensure the updated StringBuilder is stored back
        Scripts[documentUri] = cachedVersion;
        return cachedVersion.ToString();
    }

    private int GetBaseCharOfLine(string content, int line)
    {
        // Line 0 starts at position 0
        if (line == 0)
        {
            return 0;
        }

        int pos = 0;
        int currentLine = 0;

        while (currentLine < line && pos < content.Length)
        {
            int newlinePos = content.IndexOf(Environment.NewLine, pos);
            if (newlinePos == -1)
            {
                // No more newlines found, return -1 to indicate line doesn't exist
                return -1;
            }
            // Move past the newline (Environment.NewLine could be \r\n or \n)
            pos = newlinePos + Environment.NewLine.Length;
            currentLine++;
        }

        return currentLine == line ? pos : -1;
    }

    public void RemoveFromCache(TextDocumentIdentifier document)
    {
        DocumentUri documentUri = document.Uri;
        Scripts.Remove(documentUri, out StringBuilder? value);
    }
}

public enum CachedScriptType
{
    Editor,
    Dependency
}

public class CachedScript
{
    public CachedScriptType Type { get; init; }
    // Thread-safe set of dependents
    public ConcurrentDictionary<DocumentUri, byte> Dependents { get; } = new();
    public required Script Script { get; init; }

    /// <summary>
    /// Hash of the last parsed content. Used to detect if content actually changed.
    /// </summary>
    public int LastContentHash { get; set; } = 0;

    /// <summary>
    /// Hash of exported symbols from last parse. Used to detect if dependents need re-analysis.
    /// </summary>
    public int LastExportedSymbolsHash { get; set; } = 0;

    /// <summary>
    /// Timestamp of last successful parse.
    /// </summary>
    public DateTime LastParsedAt { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Whether exported symbols changed during the last parse, requiring dependent re-analysis.
    /// </summary>
    public bool ExportedSymbolsChanged { get; set; } = true;
}

public readonly record struct LoadedScript(DocumentUri Uri, Script Script);

public class ScriptManager
{
    private readonly ScriptCache _cache;
    private readonly ILogger<ScriptManager> _logger;
    private readonly ILanguageServerFacade? _facade; // added

    private ConcurrentDictionary<DocumentUri, CachedScript> Scripts { get; } = new();

    /// <summary>
    /// Global symbol registry for workspace-wide symbol deduplication and O(1) lookup.
    /// </summary>
    private readonly GlobalSymbolRegistry _symbolRegistry = new();

    /// <summary>
    /// Provides read-only access to the global symbol registry for other components.
    /// </summary>
    public GlobalSymbolRegistry SymbolRegistry => _symbolRegistry;

    /// <summary>
    /// Optional user-configured custom path to the "raw" folder for path completions.
    /// This can be set via LSP configuration and overrides automatic detection.
    /// </summary>
    public string? CustomRawPath { get; set; }

    // Ensure only one parse per script at a time
    private readonly ConcurrentDictionary<DocumentUri, SemaphoreSlim> _parseLocks = new();
    // Ensure only one analysis/merge per script at a time
    private readonly ConcurrentDictionary<DocumentUri, SemaphoreSlim> _analysisLocks = new();

    // Editor priority gate: held during editor operations to pause the indexer dispatch loop
    private readonly SemaphoreSlim _editorPriority = new(1, 1);

    public ScriptManager(ILogger<ScriptManager> logger, ILanguageServerFacade? facade = null)
    {
        _cache = new();
        _logger = logger;
        _facade = facade; // added
    }

    public async Task<IEnumerable<Diagnostic>> AddEditorAsync(TextDocumentItem document, CancellationToken cancellationToken = default)
    {
        await _editorPriority.WaitAsync(cancellationToken);
        try
        {
            string content = _cache.AddToCache(document);
            Script script = GetEditor(document);

            return await ProcessEditorAsync(document.Uri.ToUri(), script, content, cancellationToken);
        }
        finally
        {
            _editorPriority.Release();
        }
    }

    public async Task<IEnumerable<Diagnostic>> UpdateEditorAsync(OptionalVersionedTextDocumentIdentifier document, IEnumerable<TextDocumentContentChangeEvent> changes, CancellationToken cancellationToken = default)
    {
        await _editorPriority.WaitAsync(cancellationToken);
        try
        {
            string updatedContent = _cache.UpdateCache(document, changes);

            // Check if content actually changed using hash comparison
            var docUri = document.Uri;
            int contentHash = updatedContent.GetHashCode();

            if (Scripts.TryGetValue(docUri, out var cached) && cached.LastContentHash == contentHash)
            {
                // Content unchanged, return cached diagnostics
                return await cached.Script.GetDiagnosticsAsync(cancellationToken);
            }

            Script script = GetEditor(document);

            return await ProcessEditorAsync(document.Uri.ToUri(), script, updatedContent, cancellationToken);
        }
        finally
        {
            _editorPriority.Release();
        }
    }

    private async Task<IEnumerable<Diagnostic>> ProcessEditorAsync(Uri documentUri, Script script, string content, CancellationToken cancellationToken = default)
    {
        using var perfTracker = new PerformanceTracker("ProcessEditor", new Dictionary<string, object>
        {
            ["File"] = Path.GetFileName(documentUri.LocalPath),
            ["ContentLength"] = content.Length
        });

        string filePath = documentUri.LocalPath;
        var docUri = DocumentUri.From(documentUri);
        int contentHash = content.GetHashCode();

        // Update cached script metadata
        if (Scripts.TryGetValue(docUri, out var cached))
        {
            cached.LastContentHash = contentHash;
            cached.LastParsedAt = DateTime.UtcNow;
        }

        perfTracker.Checkpoint("Pre-Parse");
        await script.ParseAsync(content);
        perfTracker.Checkpoint("Post-Parse");

        // Populate global symbol registry with this script's definitions (returns true if changed)
        bool symbolsChanged = PopulateSymbolRegistry(filePath, script);

        // Track if exported symbols changed for dependency invalidation
        if (cached is not null)
        {
            cached.ExportedSymbolsChanged = symbolsChanged;
        }

        // Snapshot dependencies to avoid collection modification during enumeration
        var dependencies = script.Dependencies.ToList();
        perfTracker.AddMetadata("DependencyCount", dependencies.Count);

        List<Task> dependencyTasks = new();

        // Now, get their dependencies and parse them.
        foreach (Uri dependency in dependencies)
        {
            dependencyTasks.Add(AddDependencyAsync(documentUri, dependency, script.LanguageId));
        }

        perfTracker.Checkpoint("Pre-Dependencies");
        await Task.WhenAll(dependencyTasks);
        perfTracker.Checkpoint("Post-Dependencies");

        // Build exported symbols
        List<IExportedSymbol> exportedSymbols = new();
        foreach (Uri dependency in dependencies)
        {
            var depDoc = DocumentUri.From(dependency);
            if (Scripts.TryGetValue(depDoc, out CachedScript? cachedScript))
            {
                await EnsureParsedAsync(depDoc, cachedScript.Script, script.LanguageId, cancellationToken);
                exportedSymbols.AddRange(await cachedScript.Script.IssueExportedSymbolsAsync(cancellationToken));
            }
        }

        // Merge symbols from dependencies (filtering, deduplication, conversion to relative paths)
        var (mergeFuncLocs, mergeClassLocs) = await MergeDependencySymbolsAsync(dependencies, filePath, cancellationToken);

        perfTracker.Checkpoint("Pre-Analysis");
        // Merge + analyse under this script's analysis lock
        var thisDoc = DocumentUri.From(documentUri);
        await WithAnalysisLockAsync(thisDoc, async () =>
        {
            if (script.DefinitionsTable is not null)
            {
                foreach (var kv in mergeFuncLocs)
                {
                    var (ns, name) = kv.Key;
                    var (filePath, range) = kv.Value;
                    script.DefinitionsTable.AddFunctionLocation(ns, name, filePath, range);
                }
                foreach (var kv in mergeClassLocs)
                {
                    var (ns, name) = kv.Key;
                    var (filePath, range) = kv.Value;
                    script.DefinitionsTable.AddClassLocation(ns, name, filePath, range);
                }
            }
            await script.AnalyseAsync(exportedSymbols, cancellationToken);
        }, cancellationToken);
        perfTracker.Checkpoint("Post-Analysis");

        var diagnostics = await script.GetDiagnosticsAsync(cancellationToken);
        perfTracker.AddMetadata("DiagnosticCount", diagnostics.Count());

        return diagnostics;
    }

    /// <summary>
    /// Collects, filters, and deduplicates symbols from dependencies.
    /// Returns merged function and class locations ready for DefinitionsTable.
    /// </summary>
    private async Task<(List<KeyValuePair<(string, string), (string, Range)>> Functions, 
                        List<KeyValuePair<(string, string), (string, Range)>> Classes)>
        MergeDependencySymbolsAsync(
            IEnumerable<Uri> dependencies, 
            string filePath,
            CancellationToken cancellationToken = default)
    {
        string? currentWorkspaceRoot = WorkspaceBoundaryFilter.GetScriptsWorkspaceRoot(filePath);
        bool currentIsInRawFolder = WorkspaceBoundaryFilter.IsInCustomRawFolder(filePath) || WorkspaceBoundaryFilter.IsInToolsRawFolder(filePath);

        // Collect symbols from dependencies and convert to relative paths (filtering happens at query time)
        var allFuncLocs = new List<(string Namespace, string Name, string FilePath, Range Range)>();
        var allClassLocs = new List<(string Namespace, string Name, string FilePath, Range Range)>();

        foreach (Uri dependency in dependencies)
        {
            var depDoc = DocumentUri.From(dependency);
            if (!Scripts.TryGetValue(depDoc, out CachedScript? depScript)) continue;

            await WithAnalysisLockAsync(depDoc, async () =>
            {
                var depTable = depScript.Script.DefinitionsTable;
                if (depTable is null) return;

                // Get all symbols from this dependency and convert to relative paths
                var depFuncLocs = depTable.GetAllFunctionLocations();
                var depClassLocs = depTable.GetAllClassLocations();

                foreach (var funcLoc in depFuncLocs)
                {
                    string funcFilePath = funcLoc.Value.FilePath;
                    var filterResult = WorkspaceBoundaryFilter.FilterSymbolLocation(funcFilePath, currentWorkspaceRoot, currentIsInRawFolder);

                    // Only include workspace-appropriate symbols
                    if (filterResult != WorkspaceBoundaryFilter.FilterResult.DifferentWorkspace)
                    {
                        string? relativePath = GSCode.Parser.Util.ScriptFileResolver.ConvertToRelativeScriptPath(funcFilePath);
                        if (relativePath != null)
                        {
                            allFuncLocs.Add((funcLoc.Key.Namespace, funcLoc.Key.Name, relativePath, funcLoc.Value.Range));
                        }
                    }
                }

                foreach (var classLoc in depClassLocs)
                {
                    string classFilePath = classLoc.Value.FilePath;
                    var filterResult = WorkspaceBoundaryFilter.FilterSymbolLocation(classFilePath, currentWorkspaceRoot, currentIsInRawFolder);

                    // Only include workspace-appropriate symbols
                    if (filterResult != WorkspaceBoundaryFilter.FilterResult.DifferentWorkspace)
                    {
                        string? relativePath = GSCode.Parser.Util.ScriptFileResolver.ConvertToRelativeScriptPath(classFilePath);
                        if (relativePath != null)
                        {
                            allClassLocs.Add((classLoc.Key.Namespace, classLoc.Key.Name, relativePath, classLoc.Value.Range));
                        }
                    }
                }

                await Task.CompletedTask;
            }, cancellationToken);
        }

        // Deduplicate: only keep one version of each symbol
        // Dictionary will keep the first entry, and ParserUtil.GetScriptFilePath will resolve using priority order
        var uniqueFunctions = new Dictionary<(string Namespace, string Name), (string FilePath, Range Range)>();
        var uniqueClasses = new Dictionary<(string Namespace, string Name), (string FilePath, Range Range)>();

        foreach (var func in allFuncLocs)
        {
            var key = (func.Namespace, func.Name);
            if (!uniqueFunctions.ContainsKey(key))
            {
                uniqueFunctions[key] = (func.FilePath, func.Range);
            }
        }

        foreach (var cls in allClassLocs)
        {
            var key = (cls.Namespace, cls.Name);
            if (!uniqueClasses.ContainsKey(key))
            {
                uniqueClasses[key] = (cls.FilePath, cls.Range);
            }
        }

        // Convert to format needed for DefinitionsTable
        var mergeFuncLocs = uniqueFunctions
            .Select(kvp => new KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>(kvp.Key, kvp.Value))
            .ToList();

        var mergeClassLocs = uniqueClasses
            .Select(kvp => new KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>(kvp.Key, kvp.Value))
            .ToList();

        return (mergeFuncLocs, mergeClassLocs);
    }

    /// <summary>
    /// Populates the global symbol registry with definitions from a parsed script.
    /// Uses incremental update to detect if symbols actually changed.
    /// </summary>
    /// <param name="filePath">The file path being updated.</param>
    /// <param name="script">The parsed script.</param>
    /// <returns>True if exported symbols changed (requiring dependent re-analysis), false otherwise.</returns>
    private bool PopulateSymbolRegistry(string filePath, Script script)
    {
        if (script.DefinitionsTable is null)
            return false;

        var defTable = script.DefinitionsTable;
        var currentNamespace = defTable.CurrentNamespace;

        // Build the list of new symbols
        var newSymbols = new List<SymbolDefinition>();

        // Add exported functions to the list
        foreach (var func in defTable.ExportedFunctions)
        {
            var loc = defTable.GetFunctionLocation(func.Namespace, func.Name);
            var range = loc?.Range ?? default;
            var actualFilePath = loc?.FilePath ?? filePath;

            newSymbols.Add(new SymbolDefinition(
                Namespace: func.Namespace,
                Name: func.Name,
                Type: ExportedSymbolType.Function,
                FilePath: actualFilePath,
                Range: range,
                Parameters: func.Overloads.FirstOrDefault()?.Parameters?.Select(p => p.Name).ToArray(),
                Flags: func.Flags?.ToArray(),
                Documentation: func.DocComment ?? func.Description,
                Symbol: func
            ));
        }

        // Add exported classes to the list
        // Note: ScrClass doesn't have a Namespace property, so we use the script's CurrentNamespace
        foreach (var cls in defTable.ExportedClasses)
        {
            var loc = defTable.GetClassLocation(currentNamespace, cls.Name);
            var range = loc?.Range ?? default;
            var actualFilePath = loc?.FilePath ?? filePath;

            newSymbols.Add(new SymbolDefinition(
                Namespace: currentNamespace,
                Name: cls.Name,
                Type: ExportedSymbolType.Class,
                FilePath: actualFilePath,
                Range: range,
                Documentation: cls.Description,
                Symbol: cls
            ));
        }

        // Use incremental update which returns whether symbols changed
        return _symbolRegistry.UpdateSymbolsForFile(filePath, newSymbols);
    }

    public void RemoveEditor(TextDocumentIdentifier document)
    {
        DocumentUri documentUri = document.Uri;

        // Remove symbols from global registry when file is closed
        string filePath = documentUri.ToUri().LocalPath;
        _symbolRegistry.RemoveSymbolsFromFile(filePath);

        // Clean up cached macro definitions for this file
        GSCode.Parser.Pre.MacroDefinitionCache.Instance.RemoveFileMacros(filePath);

        Scripts.Remove(documentUri, out _);
        CleanupLocksForUri(documentUri);

        RemoveDependent(documentUri);
    }

    public Script? GetParsedEditor(TextDocumentIdentifier document)
    {
        return Scripts.TryGetValue(document.Uri, out var script) ? script.Script : null;
    }

    /// <summary>
    /// Gets the total number of loaded scripts (editor and dependency scripts).
    /// </summary>
    public int GetLoadedScriptCount()
    {
        return Scripts.Count;
    }

    /// <summary>
    /// Gets detailed script counts by file type (GSC, CSC).
    /// Note: GSH files are not loaded as scripts, use MacroDefinitionCache for GSH counts.
    /// </summary>
    public (int GscFiles, int CscFiles) GetScriptCountsByType()
    {
        int gscCount = 0;
        int cscCount = 0;

        foreach (var kvp in Scripts)
        {
            string filePath = kvp.Key.ToUri().LocalPath;
            if (filePath.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase))
            {
                gscCount++;
            }
            else if (filePath.EndsWith(".csc", StringComparison.OrdinalIgnoreCase))
            {
                cscCount++;
            }
        }

        return (gscCount, cscCount);
    }

    /// <summary>
    /// Try to find a symbol (function or class) in the global symbol registry. O(1) lookup.
    /// If ns is provided, search that namespace first. Falls back to name-only search.
    /// Returns a Location or null.
    /// </summary>
    public Location? FindSymbolLocation(string? ns, string name, string? currentFilePath = null)
    {
        // Use global registry for O(1) lookup
        var symbol = _symbolRegistry.FindSymbol(ns, name);
        if (symbol is not null)
        {
            // Resolve relative path using existing dependency resolution logic (same as Script.GetDefinitionAsync)
            string? resolvedPath = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, symbol.FilePath);

            if (resolvedPath != null && File.Exists(resolvedPath))
            {
                return new Location() { Uri = new Uri(resolvedPath), Range = symbol.Range };
            }
        }

        // Fallback to per-script lookup for symbols not yet in registry
        foreach (KeyValuePair<DocumentUri, CachedScript> kvp in Scripts)
        {
            CachedScript cached = kvp.Value;
            if (cached.Script.DefinitionsTable is null)
                continue;

            // Try with namespace if provided
            if (ns is not null)
            {
                var funcLoc = cached.Script.DefinitionsTable.GetFunctionLocation(ns, name);
                if (funcLoc is not null)
                {
                    string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, funcLoc.Value.FilePath);
                    if (resolved != null && File.Exists(resolved))
                    {
                        return new Location() { Uri = new Uri(resolved), Range = funcLoc.Value.Range };
                    }
                }

                var classLoc = cached.Script.DefinitionsTable.GetClassLocation(ns, name);
                if (classLoc is not null)
                {
                    string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, classLoc.Value.FilePath);
                    if (resolved != null && File.Exists(resolved))
                    {
                        return new Location() { Uri = new Uri(resolved), Range = classLoc.Value.Range };
                    }
                }
            }

            // Try any namespace
            var funcAny = cached.Script.DefinitionsTable.GetFunctionLocationAnyNamespace(name);
            if (funcAny is not null)
            {
                string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, funcAny.Value.FilePath);
                if (resolved != null && File.Exists(resolved))
                {
                    return new Location() { Uri = new Uri(resolved), Range = funcAny.Value.Range };
                }
            }

            var classAny = cached.Script.DefinitionsTable.GetClassLocationAnyNamespace(name);
            if (classAny is not null)
            {
                string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, classAny.Value.FilePath);
                if (resolved != null && File.Exists(resolved))
                {
                    return new Location() { Uri = new Uri(resolved), Range = classAny.Value.Range };
                }
            }
        }

        return null;
    }

#if PREVIEW
    private async Task<IEnumerable<IExportedSymbol>> AddEditorDependenciesAsync(Uri editorUri, List<Uri> dependencyUris)
    {
        List<Task<IEnumerable<IExportedSymbol>>> scriptTasks = new(dependencyUris.Count);

        // Wait for all dependencies to finish processing if they haven't already, then get their exported symbols.
        foreach (Uri dependency in dependencyUris)
        {
            Script script = AddDependency(editorUri, dependency);

            scriptTasks.Add(script.IssueExportedSymbolsAsync());
        }

        // Wait for all tasks to complete and collect their results
        IEnumerable<IExportedSymbol>[] results = await Task.WhenAll(scriptTasks);

        // Merge all exported symbols into a single IEnumerable
        IEnumerable<IExportedSymbol> merged;
        if (results.Length > 0)
        {
            merged = results.Aggregate((acc, e) => acc.Union(e));
        }
        else
        {
            merged = Array.Empty<IExportedSymbol>();
        }

        return merged;
    }
#endif

    private async Task EnsureParsedAsync(DocumentUri docUri, Script script, string? languageId, CancellationToken cancellationToken)
    {
        var gate = _parseLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!script.Parsed)
            {
                // Read file from disk and parse
                string path = docUri.ToUri().LocalPath;
                string content = await File.ReadAllTextAsync(path, cancellationToken);
                await script.ParseAsync(content);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WithAnalysisLockAsync(DocumentUri docUri, Func<Task> action, CancellationToken cancellationToken = default)
    {
        var gate = _analysisLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Script> AddDependencyAsync(Uri dependentUri, Uri uri, string languageId)
    {
        var docUri = DocumentUri.From(uri);
        bool isNewDependency = false;

        var cached = Scripts.GetOrAdd(docUri, key =>
        {
            isNewDependency = true;
            return new CachedScript
            {
                Type = CachedScriptType.Dependency,
                Script = new Script(key, languageId, _symbolRegistry, ScriptMode.Index)
            };
        });

        // Only parse if new dependency or not yet parsed
        if (isNewDependency || !cached.Script.Parsed)
        {
            await EnsureParsedAsync(docUri, cached.Script, languageId, CancellationToken.None);

            // Populate global symbol registry with dependency's definitions
            string filePath = uri.LocalPath;
            bool symbolsChanged = PopulateSymbolRegistry(filePath, cached.Script);

            // Track change state
            cached.ExportedSymbolsChanged = symbolsChanged;
            cached.LastParsedAt = DateTime.UtcNow;
        }

        cached.Dependents.TryAdd(DocumentUri.From(dependentUri), 0);

        return cached.Script;
    }

    private void RemoveDependent(DocumentUri dependentUri)
    {
        foreach (KeyValuePair<DocumentUri, CachedScript> script in Scripts)
        {
            var dependents = script.Value.Dependents;
            if (dependents.TryRemove(dependentUri, out _))
            {
                // Housekeeping
                if (dependents.IsEmpty && script.Value.Type == CachedScriptType.Dependency)
                {
                    Scripts.Remove(script.Key, out _);
                    CleanupLocksForUri(script.Key);
                }
            }
        }
    }

    private void CleanupLocksForUri(DocumentUri uri)
    {
        if (_parseLocks.TryRemove(uri, out var parseLock))
            parseLock.Dispose();
        if (_analysisLocks.TryRemove(uri, out var analysisLock))
            analysisLock.Dispose();
    }

    private Script GetEditor(TextDocumentIdentifier document)
    {
        return GetEditorByUri(document.Uri);
    }

    private Script GetEditor(TextDocumentItem document)
    {
        return GetEditorByUri(document.Uri, document.LanguageId);
    }

    private Script GetEditorByUri(DocumentUri uri, string? languageId = null)
    {
        var cached = Scripts.GetOrAdd(uri, key => new CachedScript()
        {
            Type = CachedScriptType.Editor,
            Script = new Script(key, languageId ?? "gsc", _symbolRegistry)
        });

        // If it was a dependency, upgrade to editor
        if (cached.Type != CachedScriptType.Editor)
        {
            var newCached = new CachedScript()
            {
                Type = CachedScriptType.Editor,
                Script = new Script(uri, languageId ?? "gsc", _symbolRegistry)
            };
            cached = Scripts.AddOrUpdate(uri, newCached, (_, _) => newCached);
        }

        return cached.Script;
    }

    public IEnumerable<LoadedScript> GetLoadedScripts()
    {
        foreach (var kv in Scripts)
        {
            yield return new LoadedScript(kv.Key, kv.Value.Script);
        }
    }

    // =========================
    // Recursive workspace indexing
    // =========================

    public async Task IndexWorkspaceAsync(string rootDirectory, CancellationToken cancellationToken = default)
    {
        using var perfTracker = new PerformanceTracker("IndexWorkspace", new Dictionary<string, object>
        {
            ["RootDirectory"] = Path.GetFileName(rootDirectory)
        });

        try
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                _logger.LogWarning("IndexWorkspace skipped: directory not found: {Root}", rootDirectory);
                return;
            }

            perfTracker.Checkpoint("Start-FileCollection");
            // Collect files first for count and deterministic iteration
            var filesList = Directory
                .EnumerateFiles(rootDirectory, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".csc", StringComparison.OrdinalIgnoreCase))
                .ToList();

            perfTracker.AddMetadata("FileCount", filesList.Count);
            perfTracker.Checkpoint("End-FileCollection");

#if DEBUG
            _logger.LogInformation("Indexing workspace under {Root}", rootDirectory);
            _logger.LogInformation("Indexing started: {Count} files", filesList.Count);
            var swAll = Stopwatch.StartNew();
#endif

            int maxDegree = Math.Max(1, Environment.ProcessorCount - 1);
            perfTracker.AddMetadata("Parallelism", maxDegree);

            using SemaphoreSlim gate = new(maxDegree, maxDegree);
            List<Task> tasks = new();

            perfTracker.Checkpoint("Start-Indexing");
            int fileIndex = 0;
            foreach (string file in filesList)
            {
                // Yield to editor operations every 10 files to reduce semaphore overhead
                if (fileIndex++ % 10 == 0)
                {
                    await _editorPriority.WaitAsync(cancellationToken);
                    _editorPriority.Release();
                }

                await gate.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(async () =>
                {
#if DEBUG
                    var fileSw = Stopwatch.StartNew();
#endif
                    string rel = Path.GetRelativePath(rootDirectory, file);
                    try
                    {
#if DEBUG
                        _logger.LogInformation("Indexing {File}", rel);
#endif
                        using var fileTracker = new PerformanceTracker("IndexFile", new Dictionary<string, object>
                        {
                            ["File"] = rel
                        });

                        await IndexFileAsync(file, cancellationToken);

#if DEBUG
                        fileSw.Stop();
                        _logger.LogInformation("Indexed {File} in {ElapsedMs} ms", rel, fileSw.ElapsedMilliseconds);
#endif
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to index {File}", file);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);
            perfTracker.Checkpoint("End-Indexing");

#if DEBUG
            swAll.Stop();
            _logger.LogInformation("Indexing completed in {ElapsedMs} ms for {Count} files", swAll.ElapsedMilliseconds, filesList.Count);
#endif
        }
        catch (OperationCanceledException)
        {
#if DEBUG
            _logger.LogInformation("Indexing cancelled");
#endif
        }
    }

    private async Task IndexFileAsync(string filePath, CancellationToken cancellationToken)
    {
        using var perfTracker = new PerformanceTracker("IndexFile-Internal", new Dictionary<string, object>
        {
            ["File"] = Path.GetFileName(filePath)
        });

        string ext = Path.GetExtension(filePath);
        string languageId = string.Equals(ext, ".csc", StringComparison.OrdinalIgnoreCase) ? "csc" : "gsc";

        DocumentUri docUri = DocumentUri.FromFileSystemPath(filePath);

        bool isNewFile = false;
        var cached = Scripts.GetOrAdd(docUri, key =>
        {
            isNewFile = true;
            return new CachedScript
            {
                Type = CachedScriptType.Dependency,
                Script = new Script(key, languageId, _symbolRegistry, ScriptMode.Index)
            };
        });

        // Skip if already parsed (unless it's a new file)
        if (!isNewFile && cached.Script.Parsed)
        {
            perfTracker.AddMetadata("Skipped", true);
            return;
        }

        perfTracker.Checkpoint("Pre-Parse");
        await EnsureParsedAsync(docUri, cached.Script, languageId, cancellationToken);
        perfTracker.Checkpoint("Post-Parse");

        // Populate global symbol registry
        bool symbolsChanged = PopulateSymbolRegistry(filePath, cached.Script);
        cached.ExportedSymbolsChanged = symbolsChanged;
        cached.LastParsedAt = DateTime.UtcNow;

        // Snapshot dependencies to avoid collection modification during enumeration
        var dependencies = cached.Script.Dependencies.ToList();
        perfTracker.AddMetadata("DependencyCount", dependencies.Count);

        perfTracker.Checkpoint("Pre-Dependencies");
        // Parse and register dependencies (AddDependencyAsync handles parse + registry internally)
        foreach (Uri dep in dependencies)
        {
            await AddDependencyAsync(docUri.ToUri(), dep, languageId);
        }
        perfTracker.Checkpoint("Post-Dependencies");

        // Check indexing mode early to avoid unnecessary work
        var indexingMode = GSCode.Parser.Configuration.CompletionConfiguration.WorkspaceIndexingMode;
        perfTracker.AddMetadata("IndexingMode", indexingMode.ToString());

        // Defensive check - IndexWorkspaceAsync should never be called with Off mode
        if (indexingMode == GSCode.Parser.Configuration.IndexingMode.Off)
        {
            _logger.LogWarning("IndexFileAsync called with Off mode for {File} - this should not happen", Path.GetFileName(filePath));
            return;
        }

        // Merge symbols from dependencies (filtering, deduplication, conversion to relative paths)
        perfTracker.Checkpoint("Pre-MergeDependencies");
        var (mergeFuncLocs, mergeClassLocs) = await MergeDependencySymbolsAsync(dependencies, filePath, cancellationToken);
        perfTracker.Checkpoint("Post-MergeDependencies");

        // Merge definition tables (needed for go-to-definition in both modes)
        await WithAnalysisLockAsync(docUri, async () =>
        {
            if (cached.Script.DefinitionsTable is not null)
            {
                Log.Debug("[WORKSPACE_INDEX] Merging {FuncCount} functions and {ClassCount} classes into DefinitionsTable for {File}",
                    mergeFuncLocs.Count, mergeClassLocs.Count, Path.GetFileName(filePath));

                foreach (var kv in mergeFuncLocs)
                {
                    var (ns, name) = kv.Key;
                    var (filePath, range) = kv.Value;

                    // Log first few for debugging
                    if (mergeFuncLocs.IndexOf(kv) < 5)
                    {
                        Log.Debug("[WORKSPACE_INDEX]   Adding function {Ns}::{Name} -> {Path}",
                            ns, name, filePath);
                    }

                    cached.Script.DefinitionsTable.AddFunctionLocation(ns, name, filePath, range);
                }
                foreach (var kv in mergeClassLocs)
                {
                    var (ns, name) = kv.Key;
                    var (filePath, range) = kv.Value;
                    cached.Script.DefinitionsTable.AddClassLocation(ns, name, filePath, range);
                }
            }
            await Task.CompletedTask;
        });

        if (indexingMode == GSCode.Parser.Configuration.IndexingMode.Partial)
        {
            // Partial indexing: signature analysis only (already done during parse)
            // Symbol registry already populated, definition tables merged
            // Skip expensive semantic analysis and exported symbols
            perfTracker.Checkpoint("Partial-Complete");
#if DEBUG
            _logger.LogDebug("Partial indexing complete for {File}", Path.GetFileName(filePath));
#endif
        }
        else if (indexingMode == GSCode.Parser.Configuration.IndexingMode.Full)
        {
            // Full mode: build exported symbols and run semantic analysis
            perfTracker.Checkpoint("Pre-ExportSymbols");
            List<IExportedSymbol> exportedSymbols = new();
            foreach (Uri dep in dependencies)
            {
                var depDoc = DocumentUri.From(dep);
                if (Scripts.TryGetValue(depDoc, out CachedScript? depScript))
                {
                    exportedSymbols.AddRange(await depScript.Script.IssueExportedSymbolsAsync(cancellationToken));
                }
            }
            perfTracker.Checkpoint("Post-ExportSymbols");

            perfTracker.Checkpoint("Pre-Analysis");
            await WithAnalysisLockAsync(docUri, async () =>
            {
                // Full semantic analysis (CFG + DFA + diagnostics)
                perfTracker.Checkpoint("Pre-AnalyseAsync");
                await cached.Script.AnalyseAsync(exportedSymbols, cancellationToken);
                perfTracker.Checkpoint("Post-AnalyseAsync");
            });
            perfTracker.Checkpoint("Post-Analysis");

            perfTracker.Checkpoint("Pre-PublishDiagnostics");
            // Publish diagnostics for indexed file (if LSP facade is available)
            await PublishDiagnosticsAsync(docUri, cached.Script, cancellationToken: cancellationToken);
            perfTracker.Checkpoint("Post-PublishDiagnostics");

            // Add diagnostic count for perf tracking
            var diagnostics = await cached.Script.GetDiagnosticsAsync(cancellationToken);
            perfTracker.AddMetadata("DiagnosticCount", diagnostics.Count);
        }
    }

    private async Task PublishDiagnosticsAsync(DocumentUri uri, Script script, int? version = null, CancellationToken cancellationToken = default)
    {
        if (_facade is null) return;
        try
        {
            var diags = await script.GetDiagnosticsAsync(cancellationToken);
            _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = uri,
                Version = version,
                Diagnostics = new Container<Diagnostic>(diags)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish diagnostics for {Uri}", uri.GetFileSystemPath());
        }
    }
}
