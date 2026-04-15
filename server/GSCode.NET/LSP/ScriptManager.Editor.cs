using Serilog;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System.Linq;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    public async Task<IEnumerable<Diagnostic>> AddEditorAsync(TextDocumentItem document, CancellationToken cancellationToken = default)
    {
        await _editorPriority.WaitAsync(cancellationToken);
        try
        {
            string content = _cache.AddToCache(document);
            Script script = GetEditor(document);

            return await ProcessEditorAsync(document.Uri, script, content, cancellationToken);
        }
        finally
        {
            _editorPriority.Release();
        }
    }

    public async Task<IEnumerable<Diagnostic>> UpdateEditorAsync(VersionedTextDocumentIdentifier document, IEnumerable<TextDocumentContentChangeEvent> changes, CancellationToken cancellationToken = default)
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

            return await ProcessEditorAsync(document.Uri, script, updatedContent, cancellationToken);
        }
        finally
        {
            _editorPriority.Release();
        }
    }

    public void RemoveEditor(TextDocumentIdentifier document)
    {
        Uri documentUri = document.Uri;

        // Remove symbols from global registry when file is closed
        string filePath = UriHelper.GetLocalPath(documentUri);
        _symbolRegistry.RemoveSymbolsFromFile(filePath);

        // Clean up cached macro definitions for this file
        GSCode.Parser.Pre.MacroDefinitionCache.Instance.RemoveFileMacros(filePath);

        Scripts.Remove(documentUri, out _);
        CleanupLocksForUri(documentUri);

        RemoveDependent(documentUri);
    }

    public Script? GetParsedEditor(Uri uri)
        => Scripts.TryGetValue(uri, out var script) ? script.Script : null;

    public Script? GetParsedEditor(TextDocumentIdentifier document)
        => GetParsedEditor(document.Uri);

    /// <summary>
    /// Re-parses every document that is currently open in the editor and republishes
    /// its diagnostics. Called when a watched file is created, changed externally, or
    /// deleted so that diagnostics such as <see cref="GSCode.Data.GSCErrorCodes.MissingUsingFile"/>
    /// are cleared (or raised) without requiring the user to manually edit each file.
    /// </summary>
    public async Task ReparseAllOpenEditorsAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot the editor URIs so we don't hold a live enumerator while awaiting
        var editorUris = Scripts
            .Where(kv => kv.Value.Type == CachedScriptType.Editor)
            .Select(kv => kv.Key)
            .ToList();

        foreach (Uri docUri in editorUris)
        {
            // Skip if the document was closed while we were iterating
            if (!_cache.TryGetContent(docUri, out string content))
            {
                continue;
            }

            await _editorPriority.WaitAsync(cancellationToken);
            try
            {
                if (!Scripts.TryGetValue(docUri, out CachedScript? cached))
                {
                    continue;
                }

                IEnumerable<Diagnostic> diags =
                    await ProcessEditorAsync(docUri, cached.Script, content, cancellationToken);

                if (_notifier is not null)
                {
                    _ = _notifier.PublishDiagnosticsAsync(docUri, diags, cancellationToken);
                }
            }
            finally
            {
                _editorPriority.Release();
            }
        }
    }

    /// <summary>
    /// Retrieves the current cached source text for the given document.
    /// Returns false if the document is not open in the editor cache.
    /// </summary>
    public bool TryGetCachedContent(Uri uri, out string content)
        => _cache.TryGetContent(uri, out content);

    private async Task<IEnumerable<Diagnostic>> ProcessEditorAsync(Uri documentUri, Script script, string content, CancellationToken cancellationToken = default)
    {
        string filePath = UriHelper.GetLocalPath(documentUri);
        int contentHash = content.GetHashCode();

        // Update cached script metadata
        if (Scripts.TryGetValue(documentUri, out var cached))
        {
            cached.LastContentHash = contentHash;
            cached.LastParsedAt = DateTime.UtcNow;
        }

        await script.ParseAsync(content);

        // Populate global symbol registry with this script's definitions (returns true if changed)
        bool symbolsChanged = PopulateSymbolRegistry(filePath, script);

        // Track if exported symbols changed for dependency invalidation
        if (cached is not null)
        {
            cached.ExportedSymbolsChanged = symbolsChanged;
        }

        // Snapshot dependencies to avoid collection modification during enumeration
        var dependencies = script.Dependencies.ToList();
        Log.Information("DEPENDENCY_RESOLVE: {FilePath} has {Count} dependencies", filePath, dependencies.Count);

        // Parse all dependencies in parallel
        List<Task> dependencyTasks = new();
        foreach (Uri dependency in dependencies)
        {
            dependencyTasks.Add(AddDependencyAsync(documentUri, dependency, script.LanguageId));
        }
        await Task.WhenAll(dependencyTasks);

        // Build exported symbols from parsed dependencies
        List<IExportedSymbol> exportedSymbols = new();
        foreach (Uri dependency in dependencies)
        {
            if (Scripts.TryGetValue(dependency, out CachedScript? cachedScript))
            {
                await EnsureParsedAsync(dependency, cachedScript.Script, script.LanguageId, cancellationToken);
                exportedSymbols.AddRange(await cachedScript.Script.IssueExportedSymbolsAsync(cancellationToken));
            }
        }

        // Merge symbols from dependencies (filtering, deduplication, conversion to relative paths)
        var (mergeFuncLocs, mergeClassLocs) = await MergeDependencySymbolsAsync(dependencies, filePath, cancellationToken);

        // Merge + analyse under this script's analysis lock
        await WithAnalysisLockAsync(documentUri, async () =>
        {
            if (script.DefinitionsTable is not null)
            {
                foreach (var kv in mergeFuncLocs)
                {
                    var (ns, name) = kv.Key;
                    var (fp, range) = kv.Value;
                    script.DefinitionsTable.AddFunctionLocation(ns, name, fp, TokenRange.FromRange(range));
                }
                foreach (var kv in mergeClassLocs)
                {
                    var (ns, name) = kv.Key;
                    var (fp, range) = kv.Value;
                    script.DefinitionsTable.AddClassLocation(ns, name, fp, TokenRange.FromRange(range));
                }
            }
            await script.AnalyseAsync(exportedSymbols, cancellationToken);
        }, cancellationToken);

        return await script.GetDiagnosticsAsync(cancellationToken);
    }

    private Script GetEditor(TextDocumentIdentifier document) => GetEditorByUri(document.Uri);

    private Script GetEditor(TextDocumentItem document) => GetEditorByUri(document.Uri, document.LanguageId);

    private Script GetEditorByUri(Uri uri, string? languageId = null)
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
}

