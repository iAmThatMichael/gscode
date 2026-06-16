using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.SA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.IO;

namespace GSCode.NET.LSP;

public partial class ScriptManager
{
    /// <summary>
    /// Populates the global symbol registry with definitions from a parsed script.
    /// Returns true if exported symbols changed (requiring dependent re-analysis).
    /// </summary>
    private bool PopulateSymbolRegistry(string filePath, Script script)
    {
        if (script.DefinitionsTable is null)
            return false;

        var defTable = script.DefinitionsTable;
        var currentNamespace = defTable.CurrentNamespace;

        var newSymbols = new List<SymbolDefinition>();

        foreach (var func in defTable.ExportedFunctions)
        {
            var loc = defTable.GetFunctionLocation(func.Namespace, func.Name);
            // Function flags (e.g. "autoexec", "private") are recorded on the DefinitionsTable
            // by SignatureAnalyser via RecordFunctionFlags, not on the ScrFunction instance itself,
            // so we read them from the table here.
            var flags = defTable.GetFunctionFlags(func.Namespace, func.Name);
            // Always store the absolute path of the defining script: cache-restored tables
            // carry relative location paths, which would defeat workspace-boundary checks.
            newSymbols.Add(new SymbolDefinition(
                Namespace: func.Namespace,
                Name: func.Name,
                Type: ExportedSymbolType.Function,
                FilePath: filePath,
                Range: loc?.Range ?? default,
                Parameters: func.Overloads.FirstOrDefault()?.Parameters?.Select(p => p.Name).ToArray(),
                Flags: flags,
                Documentation: func.DocComment ?? func.Description,
                Symbol: func
            ));
        }

        // Note: ScrClass doesn't carry a Namespace property; use the script's current namespace
        foreach (var cls in defTable.ExportedClasses)
        {
            var loc = defTable.GetClassLocation(currentNamespace, cls.Name);
            newSymbols.Add(new SymbolDefinition(
                Namespace: currentNamespace,
                Name: cls.Name,
                Type: ExportedSymbolType.Class,
                FilePath: filePath,
                Range: loc?.Range ?? default,
                Documentation: cls.Description,
                Symbol: cls
            ));
        }

        return GetSymbolRegistry(script.Language).UpdateSymbolsForFile(filePath, newSymbols);
    }

    /// <summary>
    /// Extracts global field accesses (level.x, world.y, game.z) from a parsed script
    /// and updates the global field registry. Call after parsing.
    /// </summary>
    private void PopulateFieldRegistry(string filePath, Script script)
    {
        var fieldAccesses = script.ExtractGlobalFieldAccesses();

        var entries = new List<(FieldOwner Owner, string FieldName)>();
        foreach (var (ownerName, fieldName) in fieldAccesses)
        {
            var owner = GlobalFieldRegistry.IdentifierToOwner(ownerName);
            if (owner.HasValue)
            {
                entries.Add((owner.Value, fieldName));
            }
        }

        GetFieldRegistry(script.Language).UpdateFieldsForFile(filePath, entries);
    }

    /// <summary>
    /// Finds a symbol (function or class) by optional namespace and name within the given language.
    /// Tries the language registry first (O(1)), falls back to per-script lookup restricted to the same language.
    /// </summary>
    public Location? FindSymbolLocation(string? ns, string name, ScriptLanguage language, string? currentFilePath = null)
    {
        var symbol = GetSymbolRegistry(language).FindSymbol(ns, name);
        if (symbol is not null)
        {
            string? resolvedPath = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, symbol.FilePath);
            if (resolvedPath != null && File.Exists(resolvedPath))
                return new Location() { Uri = new Uri(resolvedPath), Range = symbol.Range.ToRange() };
        }

        // Fallback to per-script lookup for symbols not yet in the registry (same language only)
        foreach (KeyValuePair<Uri, CachedScript> kvp in GetScripts(language))
        {
            CachedScript cached = kvp.Value;
            if (cached.Script.DefinitionsTable is null) continue;

            if (ns is not null)
            {
                var funcLoc = cached.Script.DefinitionsTable.GetFunctionLocation(ns, name);
                if (funcLoc is not null)
                {
                    string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, funcLoc.Value.FilePath);
                    if (resolved != null && File.Exists(resolved))
                        return new Location() { Uri = new Uri(resolved), Range = funcLoc.Value.Range.ToRange() };
                }

                var classLoc = cached.Script.DefinitionsTable.GetClassLocation(ns, name);
                if (classLoc is not null)
                {
                    string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, classLoc.Value.FilePath);
                    if (resolved != null && File.Exists(resolved))
                        return new Location() { Uri = new Uri(resolved), Range = classLoc.Value.Range.ToRange() };
                }
            }

            var funcAny = cached.Script.DefinitionsTable.GetFunctionLocationAnyNamespace(name);
            if (funcAny is not null)
            {
                string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, funcAny.Value.FilePath);
                if (resolved != null && File.Exists(resolved))
                    return new Location() { Uri = new Uri(resolved), Range = funcAny.Value.Range.ToRange() };
            }

            var classAny = cached.Script.DefinitionsTable.GetClassLocationAnyNamespace(name);
            if (classAny is not null)
            {
                string? resolved = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(currentFilePath ?? string.Empty, classAny.Value.FilePath);
                if (resolved != null && File.Exists(resolved))
                    return new Location() { Uri = new Uri(resolved), Range = classAny.Value.Range.ToRange() };
            }
        }

        return null;
    }

    /// <summary>Gets the total number of loaded scripts (editor and dependency).</summary>
    public int GetLoadedScriptCount() => GscScripts.Count + CscScripts.Count;

    /// <summary>
    /// Searches all indexed symbols whose names contain <paramref name="query"/>
    /// and returns resolved (definition, URI, range) tuples ready for the LSP handler.
    /// Game-relative paths are resolved using any loaded script as the base anchor.
    /// Symbols whose files cannot be resolved are silently skipped.
    /// </summary>
    public IEnumerable<(SymbolDefinition Symbol, Uri Uri, Range Range)> SearchWorkspaceSymbols(
        string query, int maxResults = 250)
    {
        // Union symbols from all language registries — workspace symbol search is language-agnostic
        var symbols = _symbolRegistries.Values
            .SelectMany(r => r.SearchSymbols(query, maxResults))
            .Take(maxResults);

        // Use any loaded script's absolute path as the base for game-relative resolution.
        string anyLoadedPath = AllScripts.FirstOrDefault().Key?.LocalPath ?? string.Empty;

        foreach (var def in symbols)
        {
            string? resolvedPath;
            if (Path.IsPathRooted(def.FilePath) && File.Exists(def.FilePath))
            {
                resolvedPath = def.FilePath;
            }
            else
            {
                resolvedPath = GSCode.Parser.Util.ScriptFileResolver.GetScriptFilePath(anyLoadedPath, def.FilePath);
                if (resolvedPath is null || !File.Exists(resolvedPath))
                    continue;
            }

            yield return (def, new Uri(resolvedPath), def.Range.ToRange());
        }
    }

    /// <summary>Gets per-language script counts.</summary>
    public (int GscFiles, int CscFiles) GetScriptCountsByType() =>
        (GscScripts.Count, CscScripts.Count);

    public IEnumerable<LoadedScript> GetLoadedScripts()
    {
        foreach (var kv in AllScripts)
            yield return new LoadedScript(kv.Key, kv.Value.Script);
    }

    /// <summary>
    /// Returns only loaded scripts for the given language.
    /// Iterates the language-scoped dictionary directly — no runtime filter.
    /// </summary>
    public IEnumerable<LoadedScript> GetLoadedScripts(ScriptLanguage language)
    {
        foreach (var kv in GetScripts(language))
            yield return new LoadedScript(kv.Key, kv.Value.Script);
    }

    /// <summary>
    /// Returns file paths that export symbols in <paramref name="namespaceName"/> for the given language.
    /// Used by code actions to suggest <c>#using</c> directives.
    /// </summary>
    public List<string> FindFilesForNamespace(ScriptLanguage language, string namespaceName) =>
        GetSymbolRegistry(language).FindFilesForNamespace(namespaceName);

    /// <summary>
    /// Returns file paths that export a specific namespaced function for the given language.
    /// </summary>
    public List<string> FindFilesForNamespacedFunction(ScriptLanguage language, string namespaceName, string functionName) =>
        GetSymbolRegistry(language).FindFilesForNamespacedFunction(namespaceName, functionName);
}

