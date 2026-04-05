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

    public List<Uri> Dependencies { get; } = new();

    // Local dictionaries for symbols defined in THIS file only (used for merging/exporting)
    // Lock protects all local dictionaries from concurrent read/write during workspace indexing.
    private readonly Lock _lock = new();
    private readonly Dictionary<QualifiedSymbolKey, (string FilePath, TokenRange Range)> _functionLocations = new();
    private readonly Dictionary<QualifiedSymbolKey, (string FilePath, TokenRange Range)> _classLocations = new();

    private readonly Dictionary<QualifiedSymbolKey, string[]> _functionParameters = new();
    private readonly Dictionary<QualifiedSymbolKey, string[]> _functionFlags = new();
    private readonly Dictionary<QualifiedSymbolKey, string?> _functionDocs = new();

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

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
    }

    public void AddFunctionLocation(string qualifier, string symbolName, string filePath, TokenRange range)
    {
        lock (_lock) _functionLocations[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = (StringPool.Intern(filePath), range);
    }

    public void AddClassLocation(string qualifier, string symbolName, string filePath, TokenRange range)
    {
        lock (_lock) _classLocations[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = (StringPool.Intern(filePath), range);
    }

    public void RecordFunctionParameters(string qualifier, string symbolName, IEnumerable<string> parameterNames)
    {
        var value = parameterNames?.Select(p => StringPool.Intern(p?.ToLowerInvariant() ?? string.Empty)).ToArray() ?? [];
        lock (_lock) _functionParameters[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = value;
    }

    public string[]? GetFunctionParameters(string qualifier, string symbolName) =>
        GetMetadata(_functionParameters, qualifier, symbolName, _globalProvider is not null ? _globalProvider.GetFunctionParameters : null);

    public void RecordFunctionFlags(string qualifier, string symbolName, IEnumerable<string> flags)
    {
        var value = flags?.Select(f => StringPool.Intern(f?.ToLowerInvariant() ?? string.Empty)).ToArray() ?? [];
        lock (_lock) _functionFlags[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = value;
    }

    public string[]? GetFunctionFlags(string qualifier, string symbolName) =>
        GetMetadata(_functionFlags, qualifier, symbolName, _globalProvider is not null ? _globalProvider.GetFunctionFlags : null);

    public void RecordFunctionDoc(string qualifier, string symbolName, string? doc)
    {
        lock (_lock) _functionDocs[QualifiedSymbolKey.Normalized(qualifier, symbolName)] = string.IsNullOrWhiteSpace(doc) ? null : doc;
    }

    public string? GetFunctionDoc(string qualifier, string symbolName)
    {
        if (_globalProvider is not null)
        {
            var result = _globalProvider.GetFunctionDoc(qualifier, symbolName);
            if (result is not null)
                return result;
        }
        lock (_lock) return _functionDocs.TryGetValue(QualifiedSymbolKey.Normalized(qualifier, symbolName), out var doc) ? doc : null;
    }

    public (string FilePath, TokenRange Range)? GetFunctionLocation(string qualifier, string symbolName) =>
        GetLocation(_functionLocations, qualifier, symbolName, _globalProvider is not null ? _globalProvider.FindFunctionLocation : null);

    public (string FilePath, TokenRange Range)? GetClassLocation(string qualifier, string symbolName) =>
        GetLocation(_classLocations, qualifier, symbolName, _globalProvider is not null ? _globalProvider.FindClassLocation : null);

    public (string FilePath, TokenRange Range)? GetFunctionLocationAnyNamespace(string symbolName) =>
        GetLocationAnyNamespace(_functionLocations, symbolName, _globalProvider is not null ? _globalProvider.FindFunctionLocationAnyNamespace : null);

    public (string FilePath, TokenRange Range)? GetClassLocationAnyNamespace(string symbolName) =>
        GetLocationAnyNamespace(_classLocations, symbolName, _globalProvider is not null ? _globalProvider.FindClassLocationAnyNamespace : null);

    public List<KeyValuePair<QualifiedSymbolKey, (string FilePath, TokenRange Range)>> GetAllFunctionLocations()
    {
        lock (_lock) return _functionLocations.ToList();
    }

    public List<KeyValuePair<QualifiedSymbolKey, (string FilePath, TokenRange Range)>> GetAllClassLocations()
    {
        lock (_lock) return _classLocations.ToList();
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

    // --- Shared lookup helpers ---

    /// <summary>
    /// Qualified location lookup: local dictionary (under lock) first, then global provider fallback.
    /// </summary>
    private (string FilePath, TokenRange Range)? GetLocation(
        Dictionary<QualifiedSymbolKey, (string FilePath, TokenRange Range)> dict,
        string? qualifier,
        string symbolName,
        Func<string, string, (string, TokenRange)?>? globalLookup)
    {
        lock (_lock)
        {
            if (qualifier is not null && dict.TryGetValue(QualifiedSymbolKey.Normalized(qualifier, symbolName), out var loc))
                return loc;
        }
        return globalLookup?.Invoke(qualifier!, symbolName);
    }

    /// <summary>
    /// Unqualified location lookup: global provider first, then linear scan of local dictionary.
    /// </summary>
    private (string FilePath, TokenRange Range)? GetLocationAnyNamespace(
        Dictionary<QualifiedSymbolKey, (string FilePath, TokenRange Range)> dict,
        string symbolName,
        Func<string, (string, TokenRange)?>? globalLookup)
    {
        var globalResult = globalLookup?.Invoke(symbolName);
        if (globalResult is not null)
            return globalResult;

        string lookup = symbolName?.ToLowerInvariant() ?? string.Empty;
        lock (_lock)
        {
            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key.SymbolName, lookup, StringComparison.Ordinal))
                    return kv.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Generic metadata lookup: global provider first, then local dictionary under lock.
    /// </summary>
    private T? GetMetadata<T>(
        Dictionary<QualifiedSymbolKey, T> dict,
        string qualifier,
        string symbolName,
        Func<string, string, T?>? globalLookup) where T : class
    {
        var globalResult = globalLookup?.Invoke(qualifier, symbolName);
        if (globalResult is not null)
            return globalResult;

        lock (_lock)
            return dict.TryGetValue(QualifiedSymbolKey.Normalized(qualifier, symbolName), out var value) ? value : default;
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
    /// Releases analysis-time data that's only needed during this script's own analysis.
    /// ExportedFunctions/Classes and location dictionaries are kept — other scripts' indexing
    /// may still read them via IssueExportedSymbolsAsync / GetAllFunctionLocations.
    /// </summary>
    internal void StripAnalysisData()
    {
        lock (_lock)
        {
            _functionParameters.Clear();
            _functionFlags.Clear();
            _functionDocs.Clear();
        }
        InternalSymbols.Clear();
        ExportedSymbols.Clear();
    }
}
