using Serilog;
using GSCode.Data;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    public async Task<IEnumerable<Diagnostic>> AddEditorAsync(TextDocumentItem document, CancellationToken cancellationToken = default)
    {
        string content;
        Script script;

        // Hold the editor priority only for the brief cache + lookup, then release
        // so the indexer dispatch loop is not blocked during the full parse.
        await _editorPriority.WaitAsync(cancellationToken);
        try
        {
            content = _cache.AddToCache(document);
            script = GetEditor(document);
        }
        finally
        {
            _editorPriority.Release();
        }

        return await ProcessEditorAsync(document.Uri.ToUri(), script, content, cancellationToken);
    }

    public async Task<IEnumerable<Diagnostic>> UpdateEditorAsync(OptionalVersionedTextDocumentIdentifier document, IEnumerable<TextDocumentContentChangeEvent> changes, CancellationToken cancellationToken = default)
    {
        string updatedContent;
        Script script;
        int contentHash;

        await _editorPriority.WaitAsync(cancellationToken);
        try
        {
            updatedContent = _cache.UpdateCache(document, changes);

            // Check if content actually changed using hash comparison
            var docUri = document.Uri.ToUri();
            contentHash = GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(updatedContent);
            ScriptLanguage docLanguage = ScriptLanguageExtensions.FromExtension(Path.GetExtension(docUri.LocalPath));

            if (UseWorkspaceCache &&
                GetScripts(docLanguage).TryGetValue(docUri, out var cached) && cached.LastContentHash == contentHash)
            {
                // Content unchanged, return cached diagnostics
                return await cached.Script.GetDiagnosticsAsync(cancellationToken);
            }

            script = GetEditor(document);
        }
        finally
        {
            _editorPriority.Release();
        }

        return await ProcessEditorAsync(document.Uri.ToUri(), script, updatedContent, cancellationToken, contentHash);
    }

    public void RemoveEditor(TextDocumentIdentifier document)
    {
        Uri documentUri = document.Uri.ToUri();

        // Remove symbols from language-scoped registry when file is closed
        string filePath = UriHelper.GetLocalPath(documentUri);
        ScriptLanguage language = ScriptLanguageExtensions.FromExtension(Path.GetExtension(filePath));
        var scripts = GetScripts(language);
        if (scripts.TryGetValue(documentUri, out var closing))
        {
            GetSymbolRegistry(closing.Script.Language).RemoveSymbolsFromFile(filePath);
            GetFieldRegistry(closing.Script.Language).RemoveFieldsFromFile(filePath);
        }

        // Clean up cached macro definitions for this file
        GSCode.Parser.Pre.MacroDefinitionCache.Instance.RemoveFileMacros(filePath);

        scripts.Remove(documentUri, out _);
        CleanupLocksForUri(documentUri);

        RemoveDependent(documentUri);

        // Remove this URI from the insert-dependents reverse map.
        foreach (var (_, consumers) in _insertDependents)
            consumers.TryRemove(documentUri, out _);
    }

    public Script? GetParsedEditor(Uri uri)
    {
        ScriptLanguage language = ScriptLanguageExtensions.FromExtension(Path.GetExtension(uri.LocalPath));
        return GetScripts(language).TryGetValue(uri, out var script) ? script.Script : null;
    }

    public Script? GetParsedEditor(TextDocumentIdentifier document)
        => GetParsedEditor(document.Uri.ToUri());

    /// <summary>
    /// Re-parses every document that is currently open in the editor and republishes
    /// its diagnostics. Called when a watched file is created, changed externally, or
    /// deleted so that diagnostics such as <see cref="GSCode.Data.GSCErrorCodes.MissingUsingFile"/>
    /// are cleared (or raised) without requiring the user to manually edit each file.
    /// </summary>
    public async Task ReparseAllOpenEditorsAsync(CancellationToken cancellationToken = default)
    {
        // Snapshot the editor URIs so we don't hold a live enumerator while awaiting
        var editorUris = AllScripts
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
                ScriptLanguage docLanguage = ScriptLanguageExtensions.FromExtension(Path.GetExtension(docUri.LocalPath));
                if (!GetScripts(docLanguage).TryGetValue(docUri, out CachedScript? cached))
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

    private async Task<IEnumerable<Diagnostic>> ProcessEditorAsync(Uri documentUri, Script script, string content, CancellationToken cancellationToken = default, int? precomputedHash = null)
    {
        string filePath = UriHelper.GetLocalPath(documentUri);
        int contentHash = precomputedHash ?? GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(content);

        // Update cached script metadata
        if (GetScripts(script.Language).TryGetValue(documentUri, out var cached))
        {
            cached.LastContentHash = contentHash;
            cached.LastParsedAt = DateTime.UtcNow;
            cached.WorkspaceCacheDirty = true;
            _workspaceCacheDirty = true;
        }

        var previousInsertPaths = script.InsertPaths.ToList();

        await script.ParseAsync(content);

        // Register/refresh this script as a consumer of its #insert files, and drop stale
        // registrations for paths no longer referenced after this edit (Script.InsertPaths only
        // reflects the current parse, so anything missing here was removed by the user).
        // Also evict each insert file's cached token list so the next parse of any
        // consumer re-reads from disk (handles the case where the insert file itself changed).
        var currentInsertPaths = script.InsertPaths;
        foreach (string staleInsertPath in previousInsertPaths)
        {
            if (currentInsertPaths.Contains(staleInsertPath, StringComparer.OrdinalIgnoreCase))
                continue;
            if (_insertDependents.TryGetValue(staleInsertPath, out var staleConsumers))
                staleConsumers.TryRemove(documentUri, out _);
        }
        foreach (string insertPath in currentInsertPaths)
        {
            _insertDependents
                .GetOrAdd(insertPath, _ => new ConcurrentDictionary<Uri, byte>(UriComparer.OrdinalIgnoreCase))
                .TryAdd(documentUri, 0);
            InsertTokenCache.Invalidate(insertPath);
        }

        // Populate global symbol registry with this script's definitions (returns true if changed)
        bool symbolsChanged = PopulateSymbolRegistry(filePath, script);

        // Populate global field registry (level.x, world.y, game.z across files)
        PopulateFieldRegistry(filePath, script);

        // Track if exported symbols changed for dependency invalidation
        if (cached is not null)
        {
            cached.ExportedSymbolsChanged = symbolsChanged;
        }

        // Snapshot dependencies to avoid collection modification during enumeration
        var dependencies = script.UsingPaths.ToList();

        // Parse all dependencies in parallel
        try
        {
            await Task.WhenAll(dependencies.Select(dep => AddDependencyAsync(documentUri, dep)));
        }
        finally
        {
            // Always signal — even on failure — so GoTo-Definition requests are never
            // left waiting on _dependenciesReady indefinitely.
            script.SignalDependenciesReady();
        }

        // Build exported symbols from parsed dependencies
        List<IExportedSymbol> exportedSymbols = new();
        foreach (Uri dependency in dependencies)
        {
            ScriptLanguage depLanguage = ScriptLanguageExtensions.FromExtension(Path.GetExtension(UriHelper.GetLocalPath(dependency)));
            if (GetScripts(depLanguage).TryGetValue(dependency, out CachedScript? cachedScript))
            {
                await EnsureParsedAsync(dependency, cachedScript.Script, cancellationToken);
                exportedSymbols.AddRange(await cachedScript.Script.IssueExportedSymbolsAsync(cancellationToken));
            }
        }

        // Merge symbols from dependencies (filtering, deduplication, conversion to relative paths)
        var (mergeFuncLocs, mergeClassLocs) = MergeDependencySymbols(dependencies, filePath);

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

        var diagnostics = await script.GetDiagnosticsAsync(cancellationToken);

        // If this script's exported symbols changed, any open editor that #insert's it
        // needs a fresh parse so it immediately sees the updated definitions.
        if (symbolsChanged && cached is not null)
        {
            foreach (Uri dependentUri in cached.Dependents.Keys)
            {
                ScriptLanguage depLang = ScriptLanguageExtensions.FromExtension(Path.GetExtension(UriHelper.GetLocalPath(dependentUri)));
                if (!GetScripts(depLang).TryGetValue(dependentUri, out var depEntry))
                    continue;
                if (depEntry.Type != CachedScriptType.Editor)
                    continue;
                if (!_cache.TryGetContent(dependentUri, out string depContent))
                    continue;

                FireAndForgetEditorReparse(dependentUri, depEntry, depContent, cancellationToken,
                    "Failed to reparse dependent {Uri} after symbol change");
            }
        }

        return diagnostics;
    }

    private Script GetEditor(TextDocumentIdentifier document)
    {
        Uri uri = document.Uri.ToUri();
        ScriptLanguage language = ScriptLanguageExtensions.FromExtension(Path.GetExtension(uri.LocalPath));
        return GetEditorByUri(uri, language);
    }

    private Script GetEditor(TextDocumentItem document)
    {
        Uri uri = document.Uri.ToUri();
        ScriptLanguage language = ScriptLanguageExtensions.FromExtension(Path.GetExtension(uri.LocalPath));
        return GetEditorByUri(uri, language);
    }

    private Script GetEditorByUri(Uri uri, ScriptLanguage language)
    {
        var scripts = GetScripts(language);
        var cached = scripts.GetOrAdd(uri, key => new CachedScript()
        {
            Type = CachedScriptType.Editor,
            Script = new Script(key, language, GetSymbolRegistry(language), globalFieldProvider: GetFieldRegistry(language))
        });

        // If it was a dependency, upgrade to editor
        if (cached.Type != CachedScriptType.Editor)
        {
            var newCached = new CachedScript()
            {
                Type = CachedScriptType.Editor,
                Script = new Script(uri, language, GetSymbolRegistry(language), globalFieldProvider: GetFieldRegistry(language))
            };
            cached = scripts.AddOrUpdate(uri, newCached, (_, _) => newCached);
        }

        return cached.Script;
    }

    private void FireAndForgetEditorReparse(Uri dependentUri, CachedScript depEntry, string depContent, CancellationToken ct, string errorTemplate)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                IEnumerable<Diagnostic> depDiags = await ProcessEditorAsync(dependentUri, depEntry.Script, depContent, ct);
                if (_notifier is not null)
                    await _notifier.PublishDiagnosticsAsync(dependentUri, depDiags, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error(ex, errorTemplate, dependentUri); }
        }, ct);
    }

    /// <summary>
    /// Called when a file that may be used as an #insert source is saved.
    /// Evicts its cached token list and re-parses every open editor that includes it,
    /// so stale spliced tokens are never used after the file changes on disk.
    /// </summary>
    public async Task NotifyInsertFileSavedAsync(string insertFilePath, CancellationToken cancellationToken = default)
    {
        // Evict the old lexed token list so the next parse re-reads from disk.
        InsertTokenCache.Invalidate(insertFilePath);

        if (!_insertDependents.TryGetValue(insertFilePath, out var consumers) || consumers.IsEmpty)
            return;

        foreach (Uri dependentUri in consumers.Keys)
        {
            ScriptLanguage depLang = ScriptLanguageExtensions.FromExtension(Path.GetExtension(UriHelper.GetLocalPath(dependentUri)));
            if (!GetScripts(depLang).TryGetValue(dependentUri, out var depEntry))
                continue;
            if (depEntry.Type != CachedScriptType.Editor)
                continue;
            if (!_cache.TryGetContent(dependentUri, out string depContent))
                continue;

            FireAndForgetEditorReparse(dependentUri, depEntry, depContent, cancellationToken,
                "Failed to reparse insert-consumer {Uri} after insert file saved");
        }
    }
}

