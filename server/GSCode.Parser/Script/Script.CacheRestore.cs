using GSCode.Parser.Cache;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;

namespace GSCode.Parser;

public partial class Script
{
    /// <summary>
    /// Restores this script from cached parse results without running the lexer, parser, or analyser.
    /// Sets Parsed and Analysed to true, populates DefinitionsTable, and registers completion source tasks.
    /// </summary>
    /// <param name="cachedData">The cached script data to restore from.</param>
    /// <param name="mode">The mode this script is being restored in (Editor or Index).</param>
    public void RestoreFromCache(CachedScriptData cachedData, ScriptMode mode)
    {
        try
        {
            // Create a minimal ParserIntelliSense for this restored script
            // In Index mode, we don't need tokens or most IntelliSense features
            Sense = new ParserIntelliSense(endLine: 0, ScriptUri, Language, mode);

            // Restore DefinitionsTable
            DefinitionsTable = new DefinitionsTable(StringPool.Intern(cachedData.CurrentNamespace), GlobalSymbolProvider);

            // Populate ExportedFunctions, merging overloads for duplicate names the same way
            // DefinitionsTable.AddFunction does during a live parse.
            // Intern Name and Namespace so cached scripts share string instances with fresh-parsed
            // ones — otherwise every deserialized ScrFunction carries its own heap string objects
            // for identifiers that would have been deduplicated by the lexer during a normal parse.
            foreach (var rawFunc in cachedData.ExportedFunctions)
            {
                // Namespace is a settable property; Name is init-only so use a with-expression.
                var func = rawFunc with { Name = StringPool.Intern(rawFunc.Name) };
                func.Namespace = StringPool.Intern(func.Namespace);
                DefinitionsTable.RestoreExportedFunction(func);
            }

            foreach (var cls in cachedData.ExportedClasses)
            {
                DefinitionsTable.ExportedClasses.Add(cls);
                DefinitionsTable.ExportedSymbols[cls.Name] = cls;
            }

            // Restore UsingPaths
            _usingPaths.Clear();
            foreach (var dep in cachedData.Dependencies)
            {
                var uri = new Uri(dep);
                DefinitionsTable.UsingPaths.Add(uri);
                _usingPaths.Add(uri);
            }

            // Restore function locations
            foreach (var kvp in cachedData.FunctionLocations)
            {
                DefinitionsTable.AddFunctionLocation(
                    kvp.Key.Qualifier,
                    kvp.Key.SymbolName,
                    kvp.Value.FilePath,
                    kvp.Value.Range,
                    kvp.Value.BodyEndLine
                );
            }

            // Restore complete function definitions
            foreach (var kvp in cachedData.FunctionDefinitions)
            {
                DefinitionsTable.RecordCompleteFunctionDefinition(kvp.Key.Qualifier, kvp.Key.SymbolName, kvp.Value);
            }

            foreach (var kvp in cachedData.ClassLocations)
            {
                DefinitionsTable.AddClassLocation(
                    kvp.Key.Qualifier,
                    kvp.Key.SymbolName,
                    kvp.Value.FilePath,
                    kvp.Value.Range,
                    kvp.Value.BodyEndLine
                );
            }

            // Restore complete class definitions
            foreach (var kvp in cachedData.ClassDefinitions)
            {
                DefinitionsTable.RecordCompleteClassDefinition(kvp.Key.Qualifier, kvp.Key.SymbolName, kvp.Value);
            }

            // Re-register macro source paths into MacroDefinitionCache so that
            // GetDetailedStatistics() reports accurate GSH file and macro counts after
            // a cache restore (preprocessing is skipped, so the cache would otherwise
            // have no entries for macros that came from #insert'd GSH files).
            // Also preserve the paths on the script so that a subsequent cache re-save
            // (ExtractCacheData) can round-trip them without zeroing out macro data.
            // Intern both the macro name and the source file path: the same macro name (e.g.
            // ANIMTREE) and the same GSH file path appear across many scripts, so sharing one
            // string instance per unique value avoids hundreds of duplicate heap allocations.
            if (cachedData.MacroDefinitions.Count > 0)
            {
                var internedMacros = new Dictionary<string, string?>(cachedData.MacroDefinitions.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (macroName, sourceFilePath) in cachedData.MacroDefinitions)
                {
                    string internedName = StringPool.Intern(macroName);
                    string? internedPath = sourceFilePath is not null ? StringPool.Intern(sourceFilePath) : null;
                    Pre.MacroDefinitionCache.Instance.TrackMacroSource(internedPath, internedName);
                    internedMacros[internedName] = internedPath;
                }
                _cachedMacroSourcePaths = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string?>(internedMacros);
            }
            else
            {
                _cachedMacroSourcePaths = null;
            }

            _references.Clear();
            foreach (var cachedReference in cachedData.References)
            {
                // Intern Namespace, Name, ClassName, ScopeId: these are high-frequency identifiers
                // that repeat across many files. During a fresh parse the lexer interns all tokens;
                // cache restoration bypasses the lexer so we do it explicitly here.
                var key = new SymbolKey(
                    cachedReference.Kind,
                    StringPool.Intern(cachedReference.Namespace),
                    StringPool.Intern(cachedReference.Name),
                    cachedReference.ClassName is not null ? StringPool.Intern(cachedReference.ClassName) : null,
                    cachedReference.ScopeId is not null ? StringPool.Intern(cachedReference.ScopeId) : null);

                if (!_references.TryGetValue(key, out var ranges))
                {
                    ranges = [];
                    _references[key] = ranges;
                }

                ranges.Add(new Range(
                    cachedReference.StartLine,
                    cachedReference.StartChar,
                    cachedReference.EndLine,
                    cachedReference.EndChar));
            }

            _cachedGlobalFieldAccesses = cachedData.GlobalFieldAccesses
                .Select(field => (StringPool.Intern(field.OwnerName), StringPool.Intern(field.FieldName)))
                .ToList();

            // Mark as parsed and analysed
            Parsed = true;
            Analysed = true;

            // Restore diagnostics into Sense so GetDiagnosticsAsync returns them correctly
            foreach (var cd in cachedData.Diagnostics)
            {
                Sense.Diagnostics.Add(new Diagnostic
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        cd.StartLine, cd.StartChar,
                        cd.EndLine, cd.EndChar),
                    Severity = cd.Severity,
                    Code = cd.Code is not null ? new OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticCode(cd.Code) : (OmniSharp.Extensions.LanguageServer.Protocol.Models.DiagnosticCode?)null,
                    Message = cd.Message,
                    Source = cd.Source
                });
            }

            // Set tasks to completed so WaitUntilParsedAsync/WaitUntilAnalysedAsync don't NPE
            ParsingTask = Task.CompletedTask;
            AnalysisTask = Task.CompletedTask;

            // Signal the current parse gate and analysis gate so waiters can proceed
            _currentParseGate.TrySetResult();
            _analysisInitiated.TrySetResult();

            Log.Debug("Restored script {Uri} from cache (namespace: {Namespace}, functions: {FuncCount}, classes: {ClassCount})",
                ScriptUri.LocalPath, cachedData.CurrentNamespace,
                cachedData.ExportedFunctions.Count, cachedData.ExportedClasses.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore script {Uri} from cache, will fall back to normal parsing", ScriptUri.LocalPath);
            // Reset state and let caller fall back to normal parsing
            Failed = true;
            Parsed = false;
            Analysed = false;
            DefinitionsTable = null;
        }
    }
}
