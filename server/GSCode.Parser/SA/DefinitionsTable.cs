using System;
using System.Collections.Generic;
using System.Linq;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

public class DefinitionsTable
{
    public string CurrentNamespace { get; set; }

    internal List<(ScrFunction Function, FunDefnNode Node)> LocalScopedFunctions { get; } = new();
    internal List<(ScrClass Class, ClassDefnNode Node)> LocalScopedClasses { get; } = new();
    public List<ScrFunction> ExportedFunctions { get; } = new();
    public List<ScrClass> ExportedClasses { get; } = new();
    public Dictionary<string, IExportedSymbol> InternalSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IExportedSymbol> ExportedSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Uri> UsingPaths { get; } = new();

    // Local dictionaries for symbols defined in THIS file only (used for merging/exporting)
    // Lock protects all local dictionaries from concurrent read/write during workspace indexing.
    private readonly Lock _lock = new();
    private readonly Dictionary<QualifiedSymbolKey, (string FilePath, TokenRange Range, int BodyEndLine)> _functionLocations = new();
    private readonly Dictionary<QualifiedSymbolKey, (string FilePath, TokenRange Range, int BodyEndLine)> _classLocations = new();

    private readonly Dictionary<QualifiedSymbolKey, CompleteFunctionDefinition> _functionDefinitions = new();
    private readonly Dictionary<QualifiedSymbolKey, CompleteClassDefinition> _classDefinitions = new();

    /// <summary>
    /// Optional global symbol registry for workspace-wide O(1) lookups.
    /// When set, lookup methods will query the global registry first before falling back to local dictionaries.
    /// </summary>
    private readonly ISymbolLocationProvider? _globalProvider;


    public DefinitionsTable(string currentNamespace, ISymbolLocationProvider? globalProvider = null)
    {
        CurrentNamespace = StringPool.Intern(currentNamespace);
        _globalProvider = globalProvider;
    }

    /// <summary>
    /// Returns true if any function defined in a file matching <paramref name="filePathSuffix"/>
    /// (case-insensitive) has the given flag. Returns false when no global provider is wired.
    /// </summary>
    public bool AnyFunctionInDependencyHasFlag(string filePathSuffix, string flag)
        => _globalProvider?.AnyFunctionInFileHasFlag(filePathSuffix, flag) ?? false;

    internal void AddFunction(ScrFunction function, FunDefnNode node)
    {
        LocalScopedFunctions.Add((function, node));

        ScrFunction internalFunction = function with { Namespace = CurrentNamespace, Implicit = true };
        string qualifiedName = $"{CurrentNamespace}::{function.Name}";

        // Merge as overload if a function with the same name already exists.
        if (InternalSymbols.TryGetValue(function.Name, out IExportedSymbol? existing) && existing is ScrFunction existingFunc)
        {
            existingFunc.Overloads.AddRange(internalFunction.Overloads);
            // Also merge into the qualified entry
            if (InternalSymbols.TryGetValue(qualifiedName, out IExportedSymbol? existingQual) && existingQual is ScrFunction existingQualFunc)
            {
                existingQualFunc.Overloads.AddRange(internalFunction.Overloads);
            }
        }
        else
        {
            InternalSymbols[function.Name] = internalFunction;
            InternalSymbols[qualifiedName] = internalFunction;
        }

        // Only add to exported functions if it's not private.
        if (!function.Private)
        {
            ScrFunction exportedFunction = function with { Namespace = CurrentNamespace };

            if (ExportedSymbols.TryGetValue(exportedFunction.Name, out IExportedSymbol? existingExport) && existingExport is ScrFunction existingExportFunc)
            {
                existingExportFunc.Overloads.AddRange(exportedFunction.Overloads);
            }
            else
            {
                ExportedFunctions.Add(exportedFunction);
                ExportedSymbols[exportedFunction.Name] = exportedFunction;
            }
        }
    }

    /// <summary>
    /// Restores an exported function from a workspace cache, merging overloads if a function
    /// of the same name was already restored (mirrors the merge <see cref="AddFunction"/> performs
    /// during a live parse, so cache-restored scripts keep the same overload-merge invariant).
    /// </summary>
    internal void RestoreExportedFunction(ScrFunction function)
    {
        if (ExportedSymbols.TryGetValue(function.Name, out IExportedSymbol? existing) && existing is ScrFunction existingFunc)
        {
            existingFunc.Overloads.AddRange(function.Overloads);
        }
        else
        {
            ExportedFunctions.Add(function);
            ExportedSymbols[function.Name] = function;
        }
    }

    internal void AddClass(ScrClass scrClass, ClassDefnNode node)
    {
        LocalScopedClasses.Add((scrClass, node));

        // Add to internal symbols for within-file references
        InternalSymbols.Add(scrClass.Name, scrClass);
        InternalSymbols.Add($"{CurrentNamespace}::{scrClass.Name}", scrClass);

        // Always export classes (GSC doesn't have private classes)
        ExportedClasses.Add(scrClass);
        ExportedSymbols.Add(scrClass.Name, scrClass);
    }

    public void AddUsingPath(string scriptPath)
    {
        UsingPaths.Add(new Uri(scriptPath));
    }

    public void AddFunctionLocation(string qualifier, string symbolName, string filePath, TokenRange range, int bodyEndLine = 0)
    {
        lock (_lock) _functionLocations[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = (StringPool.Intern(filePath), range, bodyEndLine);
    }

    public void AddClassLocation(string qualifier, string symbolName, string filePath, TokenRange range, int bodyEndLine = 0)
    {
        lock (_lock) _classLocations[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = (StringPool.Intern(filePath), range, bodyEndLine);
    }

    private void UpdateFunctionDefinition(string qualifier, string symbolName,
        Func<CompleteFunctionDefinition, CompleteFunctionDefinition> updater)
    {
        lock (_lock)
        {
            var key = QualifiedSymbolKey.Normalized(qualifier, symbolName);
            if (!_functionDefinitions.TryGetValue(key, out var def))
                def = new CompleteFunctionDefinition { Name = symbolName, Namespace = qualifier, LocalScriptPath = string.Empty, SourceRange = new Range(new Position(0, 0), new Position(0, 0)) };
            _functionDefinitions[key] = updater(def);
        }
    }

    public void RecordFunctionParameters(string qualifier, string symbolName, IEnumerable<FunctionParameter> parameters)
    {
        var value = parameters?.ToArray() ?? [];
        UpdateFunctionDefinition(qualifier, symbolName, def => def with { Parameters = value });
    }

    public FunctionParameter[]? GetFunctionParameters(string qualifier, string symbolName)
    {
        var globalResult = _globalProvider?.GetFunctionParameters(qualifier, symbolName);
        if (globalResult is not null) return globalResult;
        lock (_lock)
        {
            var key = QualifiedSymbolKey.Normalized(qualifier, symbolName);
            return _functionDefinitions.TryGetValue(key, out var def) ? def.Parameters : null;
        }
    }

    public void RecordFunctionFlags(string qualifier, string symbolName, IEnumerable<string> flags)
    {
        var value = flags?.Select(f => StringPool.Intern(f?.ToLowerInvariant() ?? string.Empty)).ToArray() ?? [];
        UpdateFunctionDefinition(qualifier, symbolName, def => def with { Flags = value });
    }

    public string[]? GetFunctionFlags(string qualifier, string symbolName)
    {
        var globalResult = _globalProvider?.GetFunctionFlags(qualifier, symbolName);
        if (globalResult is not null) return globalResult;
        lock (_lock)
        {
            var key = QualifiedSymbolKey.Normalized(qualifier, symbolName);
            return _functionDefinitions.TryGetValue(key, out var def) ? def.Flags : null;
        }
    }

    public void RecordFunctionDoc(string qualifier, string symbolName, string? doc)
        => UpdateFunctionDefinition(qualifier, symbolName,
            def => def with { DocComment = string.IsNullOrWhiteSpace(doc) ? null : doc });

    public string? GetFunctionDoc(string qualifier, string symbolName)
    {
        var globalResult = _globalProvider?.GetFunctionDoc(qualifier, symbolName);
        if (globalResult is not null) return globalResult;
        lock (_lock)
        {
            var key = QualifiedSymbolKey.Normalized(qualifier, symbolName);
            return _functionDefinitions.TryGetValue(key, out var def) ? def.DocComment : null;
        }
    }

    /// <summary>
    /// Gets the complete function definition for a qualified symbol.
    /// </summary>
    public CompleteFunctionDefinition? GetFunctionDefinition(string qualifier, string symbolName)
    {
        lock (_lock)
        {
            var key = QualifiedSymbolKey.Normalized(qualifier, symbolName);
            return _functionDefinitions.TryGetValue(key, out var def) ? def : null;
        }
    }

    /// <summary>
    /// Records a complete function definition, replacing any existing entry.
    /// </summary>
    internal void RecordCompleteFunctionDefinition(string qualifier, string symbolName, CompleteFunctionDefinition definition)
    {
        lock (_lock) _functionDefinitions[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = definition;
    }

    /// <summary>
    /// Merges analysis-collected data (variables, field assignments) into the existing definition seeded by SignatureAnalyser.
    /// Does not overwrite identity or location fields already set by SignatureAnalyser.
    /// </summary>
    internal void MergeAnalysisDataIntoDefinition(string qualifier, string symbolName, FunctionLocal[] variables,
        FunctionFieldAssignment[] fieldAssignments)
    {
        lock (_lock)
        {
            var key = QualifiedSymbolKey.Normalized(qualifier, symbolName);
            if (!_functionDefinitions.TryGetValue(key, out var def)) return;
            _functionDefinitions[key] = def with
            {
                Variables = variables,
                FieldAssignments = fieldAssignments
            };
        }
    }

    public (string FilePath, TokenRange Range)? GetFunctionLocation(string qualifier, string symbolName)
    {
        lock (_lock)
        {
            if (qualifier is not null && _functionLocations.TryGetValue(QualifiedSymbolKey.Normalized(qualifier, symbolName), out var loc))
                return (loc.FilePath, loc.Range);
        }
        return _globalProvider?.FindFunctionLocation(qualifier!, symbolName);
    }

    public (string FilePath, TokenRange Range)? GetClassLocation(string qualifier, string symbolName)
    {
        lock (_lock)
        {
            if (qualifier is not null && _classLocations.TryGetValue(QualifiedSymbolKey.Normalized(qualifier, symbolName), out var loc))
                return (loc.FilePath, loc.Range);
        }
        return _globalProvider?.FindClassLocation(qualifier!, symbolName);
    }

    /// <summary>
    /// Returns the declaration location for a function or class with the given qualified name,
    /// checking functions first then classes. Returns <see langword="null"/> if neither is found.
    /// </summary>
    public (string FilePath, TokenRange Range)? GetSymbolLocation(string qualifier, string symbolName) =>
        GetFunctionLocation(qualifier, symbolName) ?? GetClassLocation(qualifier, symbolName);

    public (string FilePath, TokenRange Range)? GetFunctionLocationAnyNamespace(string symbolName)
    {
        var globalResult = _globalProvider?.FindFunctionLocationAnyNamespace(symbolName);
        if (globalResult is not null) return globalResult;
        string lookup = symbolName?.ToLowerInvariant() ?? string.Empty;
        lock (_lock)
        {
            foreach (var kv in _functionLocations)
            {
                if (string.Equals(kv.Key.SymbolName, lookup, StringComparison.Ordinal))
                    return (kv.Value.FilePath, kv.Value.Range);
            }
        }
        return null;
    }

    public (string FilePath, TokenRange Range)? GetClassLocationAnyNamespace(string symbolName)
    {
        var globalResult = _globalProvider?.FindClassLocationAnyNamespace(symbolName);
        if (globalResult is not null) return globalResult;
        string lookup = symbolName?.ToLowerInvariant() ?? string.Empty;
        lock (_lock)
        {
            foreach (var kv in _classLocations)
            {
                if (string.Equals(kv.Key.SymbolName, lookup, StringComparison.Ordinal))
                    return (kv.Value.FilePath, kv.Value.Range);
            }
        }
        return null;
    }

    public List<KeyValuePair<QualifiedSymbolKey, (string FilePath, TokenRange Range, int BodyEndLine)>> GetAllFunctionLocations()
    {
        lock (_lock) return _functionLocations.ToList();
    }

    public List<KeyValuePair<QualifiedSymbolKey, (string FilePath, TokenRange Range, int BodyEndLine)>> GetAllClassLocations()
    {
        lock (_lock) return _classLocations.ToList();
    }

    internal void RecordCompleteClassDefinition(string qualifier, string symbolName, CompleteClassDefinition definition)
    {
        lock (_lock) _classDefinitions[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = definition;
    }

    public CompleteClassDefinition? GetClassDefinition(string qualifier, string symbolName)
    {
        lock (_lock)
        {
            var key = QualifiedSymbolKey.Normalized(qualifier, symbolName);
            return _classDefinitions.TryGetValue(key, out var def) ? def : null;
        }
    }

    public List<KeyValuePair<QualifiedSymbolKey, CompleteClassDefinition>> GetAllClassDefinitions()
    {
        lock (_lock) return _classDefinitions.ToList();
    }

    /// <summary>
    /// Iterates function locations under the lock without allocating a copy.
    /// </summary>
    public void VisitFunctionLocations(Action<QualifiedSymbolKey, string, TokenRange> visitor)
    {
        lock (_lock)
        {
            foreach (var kvp in _functionLocations)
                visitor(kvp.Key, kvp.Value.FilePath, kvp.Value.Range);
        }
    }

    /// <summary>
    /// Iterates class locations under the lock without allocating a copy.
    /// </summary>
    public void VisitClassLocations(Action<QualifiedSymbolKey, string, TokenRange> visitor)
    {
        lock (_lock)
        {
            foreach (var kvp in _classLocations)
                visitor(kvp.Key, kvp.Value.FilePath, kvp.Value.Range);
        }
    }

    /// <summary>
    /// Gets a method on a specific class by name.
    /// Searches both the class's methods and its parent class chain.
    /// </summary>
    public ScrFunction? GetMethodOnClass(string className, string methodName)
    {
        ScrClass? scrClass = FindLocalClass(className);

        if (scrClass is not null)
        {
            return FindMethodInClassHierarchy(scrClass, methodName);
        }

        return null;
    }

    /// <summary>
    /// Finds a class by name in local scope or internal symbols.
    /// </summary>
    private ScrClass? FindLocalClass(string className)
    {
        var localEntry = LocalScopedClasses.FirstOrDefault(t =>
            string.Equals(t.Class.Name, className, StringComparison.OrdinalIgnoreCase));

        if (localEntry.Class is not null)
        {
            return localEntry.Class;
        }

        if (InternalSymbols.TryGetValue(className, out var symbol) && symbol is ScrClass scrClass)
        {
            return scrClass;
        }

        return null;
    }

    /// <summary>
    /// Recursively searches for a method in a class and its parent chain.
    /// </summary>
    private ScrFunction? FindMethodInClassHierarchy(ScrClass scrClass, string methodName)
    {
        // Search direct methods on this class
        var method = scrClass.Methods.FirstOrDefault(m =>
            string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));

        if (method is not null)
        {
            return method;
        }

        // If not found and class has a parent, search the parent
        if (!string.IsNullOrEmpty(scrClass.InheritsFrom))
        {
            ScrClass? parentClass = FindLocalClass(scrClass.InheritsFrom);

            if (parentClass is not null)
            {
                return FindMethodInClassHierarchy(parentClass, methodName);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the ScrClass object by name, searching local and exported symbols.
    /// </summary>
    public ScrClass? GetClassByName(string className)
    {
        return FindLocalClass(className);
    }

    /// <summary>
    /// Releases AST node references from LocalScopedFunctions/Classes.
    /// Should be called after CFA/DFA analysis completes and these are no longer needed.
    /// </summary>
    internal void StripAstReferences()
    {
        LocalScopedFunctions.Clear();
        LocalScopedClasses.Clear();
    }

    /// <summary>
    /// Releases per-script location dictionaries after the global registry has been populated.
    /// </summary>
    internal void StripLocationData()
    {
        lock (_lock)
        {
            _functionLocations.Clear();
            _functionLocations.TrimExcess();
            _classLocations.Clear();
            _classLocations.TrimExcess();
        }
    }

    /// <summary>
    /// Releases analysis-time data that's only needed during this script's own analysis.
    /// ExportedFunctions/Classes and location dictionaries are kept — other scripts' indexing
    /// may still read them via IssueExportedSymbolsAsync / GetAllFunctionLocations.
    /// </summary>
    internal void StripAnalysisData()
    {
        lock (_lock)
        {
            _functionDefinitions.Clear();
            _classDefinitions.Clear();
        }
        InternalSymbols.Clear();
        ExportedSymbols.Clear();
    }
}

