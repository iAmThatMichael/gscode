using Serilog;
using GSCode.Data;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
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

        await _editorPriority.WaitAsync(cancellationToken);
        try
        {
            updatedContent = _cache.UpdateCache(document, changes);

            // Check if content actually changed using hash comparison
            var docUri = document.Uri.ToUri();
            int contentHash = GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(updatedContent);
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

        return await ProcessEditorAsync(document.Uri.ToUri(), script, updatedContent, cancellationToken);
    }

    public void RemoveEditor(TextDocumentIdentifier document)
    {
        Uri documentUri = document.Uri.ToUri();

        // Remove symbols from language-scoped registry when file is closed
        string filePath = UriHelper.GetLocalPath(documentUri);
        ScriptLanguage language = ScriptLanguageExtensions.FromExtension(Path.GetExtension(documentUri.LocalPath));
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

    private async Task<IEnumerable<Diagnostic>> ProcessEditorAsync(Uri documentUri, Script script, string content, CancellationToken cancellationToken = default)
    {
        string filePath = UriHelper.GetLocalPath(documentUri);
        int contentHash = GSCode.Parser.Cache.WorkspaceCacheManager.GetDeterministicHashCode(content);

        // Update cached script metadata
        if (GetScripts(script.Language).TryGetValue(documentUri, out var cached))
        {
            cached.LastContentHash = contentHash;
            cached.LastParsedAt = DateTime.UtcNow;
            cached.WorkspaceCacheDirty = true;
            _workspaceCacheDirty = true;
        }

        await script.ParseAsync(content);

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
        var dependencies = script.Dependencies.ToList();
        Log.Debug("DEPENDENCY_RESOLVE: {FilePath} has {Count} dependencies", filePath, dependencies.Count);

        // Parse all dependencies in parallel
        List<Task> dependencyTasks = new();
        foreach (Uri dependency in dependencies)
        {
            dependencyTasks.Add(AddDependencyAsync(documentUri, dependency));
        }
        await Task.WhenAll(dependencyTasks);

        // Build exported symbols from parsed dependencies
        List<IExportedSymbol> exportedSymbols = new();
        foreach (Uri dependency in dependencies)
        {
            ScriptLanguage depLanguage = ScriptLanguageExtensions.FromExtension(Path.GetExtension(dependency.LocalPath));
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

        return await script.GetDiagnosticsAsync(cancellationToken);
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
        // Prefer LSP-supplied languageId when available; fall back to extension.
        ScriptLanguage language = document.LanguageId is not null
            ? ScriptLanguageExtensions.FromString(document.LanguageId)
            : ScriptLanguageExtensions.FromExtension(Path.GetExtension(uri.LocalPath));
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
}

