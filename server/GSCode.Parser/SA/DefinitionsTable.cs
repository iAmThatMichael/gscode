using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

public class DefinitionsTable
{
    public string CurrentNamespace { get; set; }

    internal List<Tuple<ScrFunction, FunDefnNode>> LocalScopedFunctions { get; } = new();
    internal List<Tuple<ScrClass, ClassDefnNode>> LocalScopedClasses { get; } = new();
    public List<ScrFunction> ExportedFunctions { get; } = new();
    public List<ScrClass> ExportedClasses { get; } = new();
    public Dictionary<string, IExportedSymbol> InternalSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IExportedSymbol> ExportedSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Uri> Dependencies { get; } = new();

    // Local dictionaries for symbols defined in THIS file only (used for merging/exporting)
    // Lock protects all local dictionaries from concurrent read/write during workspace indexing.
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Namespace, string Name), (string FilePath, TokenRange Range)> _functionLocations = new();
    private readonly Dictionary<(string Namespace, string Name), (string FilePath, TokenRange Range)> _classLocations = new();

    private readonly Dictionary<(string Namespace, string Name), string[]> _functionParameters = new();
    private readonly Dictionary<(string Namespace, string Name), string[]> _functionFlags = new();
    private readonly Dictionary<(string Namespace, string Name), string?> _functionDocs = new();

    /// <summary>
    /// Optional global symbol registry for workspace-wide O(1) lookups.
    /// When set, lookup methods will query the global registry first before falling back to local dictionaries.
    /// </summary>
    private readonly ISymbolLocationProvider? _globalProvider;

    private static (string Namespace, string Name) NK(string ns, string name)
        => (StringPool.Intern(ns?.ToLowerInvariant() ?? string.Empty), StringPool.Intern(name?.ToLowerInvariant() ?? string.Empty));

    public DefinitionsTable(string currentNamespace, ISymbolLocationProvider? globalProvider = null)
    {
        CurrentNamespace = StringPool.Intern(currentNamespace);
        _globalProvider = globalProvider;
    }

    internal void AddFunction(ScrFunction function, FunDefnNode node)
    {
        LocalScopedFunctions.Add(new Tuple<ScrFunction, FunDefnNode>(function, node));

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
        LocalScopedClasses.Add(new Tuple<ScrClass, ClassDefnNode>(scrClass, node));

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

    public void AddFunctionLocation(string ns, string name, string filePath, TokenRange range)
    {
        lock (_lock) _functionLocations[NK(ns, name)] = (StringPool.Intern(filePath), range);
    }

    public void AddClassLocation(string ns, string name, string filePath, TokenRange range)
    {
        lock (_lock) _classLocations[NK(ns, name)] = (StringPool.Intern(filePath), range);
    }

    public void RecordFunctionParameters(string ns, string name, IEnumerable<string> parameterNames)
    {
        var value = parameterNames?.Select(p => StringPool.Intern(p?.ToLowerInvariant() ?? string.Empty)).ToArray() ?? Array.Empty<string>();
        lock (_lock) _functionParameters[NK(ns, name)] = value;
    }

    public string[]? GetFunctionParameters(string ns, string name)
    {
        if (_globalProvider is not null)
        {
            var result = _globalProvider.GetFunctionParameters(ns, name);
            if (result is not null)
                return result;
        }
        lock (_lock) return _functionParameters.TryGetValue(NK(ns, name), out var list) ? list : null;
    }

    public void RecordFunctionFlags(string ns, string name, IEnumerable<string> flags)
    {
        var value = flags?.Select(f => StringPool.Intern(f?.ToLowerInvariant() ?? string.Empty)).ToArray() ?? Array.Empty<string>();
        lock (_lock) _functionFlags[NK(ns, name)] = value;
    }

    public string[]? GetFunctionFlags(string ns, string name)
    {
        if (_globalProvider is not null)
        {
            var result = _globalProvider.GetFunctionFlags(ns, name);
            if (result is not null)
                return result;
        }
        lock (_lock) return _functionFlags.TryGetValue(NK(ns, name), out var list) ? list : null;
    }

    public void RecordFunctionDoc(string ns, string name, string? doc)
    {
        lock (_lock) _functionDocs[NK(ns, name)] = string.IsNullOrWhiteSpace(doc) ? null : doc;
    }

    public string? GetFunctionDoc(string ns, string name)
    {
        if (_globalProvider is not null)
        {
            var result = _globalProvider.GetFunctionDoc(ns, name);
            if (result is not null)
                return result;
        }
        lock (_lock) return _functionDocs.TryGetValue(NK(ns, name), out var doc) ? doc : null;
    }

    public (string FilePath, TokenRange Range)? GetFunctionLocation(string ns, string name)
    {
        // Check local dictionary first (current file has priority)
        if (ns is not null && _functionLocations.TryGetValue(NK(ns, name), out var loc))
        {
            return loc;
        }
        // Fall back to global provider for O(1) lookup across workspace
        if (_globalProvider is not null)
        {
            var result = _globalProvider.FindFunctionLocation(ns, name);
            if (result is not null)
                return result;
        }
        return null;
    }

    public (string FilePath, TokenRange Range)? GetClassLocation(string ns, string name)
    {
        if (_globalProvider is not null)
        {
            var result = _globalProvider.FindClassLocation(ns, name);
            if (result is not null)
                return result;
        }
        lock (_lock)
        {
            if (ns is not null && _classLocations.TryGetValue(NK(ns, name), out var loc))
                return loc;
            return null;
        }
    }

    public (string FilePath, TokenRange Range)? GetFunctionLocationAnyNamespace(string name)
    {
        if (_globalProvider is not null)
        {
            var result = _globalProvider.FindFunctionLocationAnyNamespace(name);
            if (result is not null)
                return result;
        }
        string lookup = name?.ToLowerInvariant() ?? string.Empty;
        lock (_lock)
        {
            foreach (var kv in _functionLocations)
            {
                if (string.Equals(kv.Key.Name, lookup, StringComparison.Ordinal))
                    return kv.Value;
            }
        }
        return null;
    }

    public (string FilePath, TokenRange Range)? GetClassLocationAnyNamespace(string name)
    {
        if (_globalProvider is not null)
        {
            var result = _globalProvider.FindClassLocationAnyNamespace(name);
            if (result is not null)
                return result;
        }
        string lookup = name?.ToLowerInvariant() ?? string.Empty;
        lock (_lock)
        {
            foreach (var kv in _classLocations)
            {
                if (string.Equals(kv.Key.Name, lookup, StringComparison.Ordinal))
                    return kv.Value;
            }
        }
        return null;
    }

    public List<KeyValuePair<(string Namespace, string Name), (string FilePath, TokenRange Range)>> GetAllFunctionLocations()
    {
        lock (_lock) return _functionLocations.ToList();
    }

    public List<KeyValuePair<(string Namespace, string Name), (string FilePath, TokenRange Range)>> GetAllClassLocations()
    {
        lock (_lock) return _classLocations.ToList();
    }

    public List<KeyValuePair<(string Namespace, string Name), string[]>> GetAllFunctionParameters()
    {
        lock (_lock) return _functionParameters.ToList();
    }

    public List<KeyValuePair<(string Namespace, string Name), string?>> GetAllFunctionDocs()
    {
        lock (_lock) return _functionDocs.ToList();
    }

    /// <summary>
    /// Gets a method on a specific class by name.
    /// Searches both the class's methods and its parent class chain.
    /// </summary>
    public ScrFunction? GetMethodOnClass(string className, string methodName)
    {
        // First try to find the class in local scope
        var localClass = LocalScopedClasses.FirstOrDefault(t => 
            string.Equals(t.Item1.Name, className, StringComparison.OrdinalIgnoreCase));

        if (localClass is not null)
        {
            return FindMethodInClassHierarchy(localClass.Item1, methodName);
        }

        // Try exported symbols (from dependencies)
        if (InternalSymbols.TryGetValue(className, out var symbol) && symbol is ScrClass scrClass)
        {
            return FindMethodInClassHierarchy(scrClass, methodName);
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
            var parentClass = LocalScopedClasses.FirstOrDefault(t => 
                string.Equals(t.Item1.Name, scrClass.InheritsFrom, StringComparison.OrdinalIgnoreCase));

            if (parentClass is not null)
            {
                return FindMethodInClassHierarchy(parentClass.Item1, methodName);
            }

            // Try from exported symbols
            if (InternalSymbols.TryGetValue(scrClass.InheritsFrom, out var parentSymbol) && 
                parentSymbol is ScrClass parentScrClass)
            {
                return FindMethodInClassHierarchy(parentScrClass, methodName);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the ScrClass object by name, searching local and exported symbols.
    /// </summary>
    public ScrClass? GetClassByName(string className)
    {
        // Try local scope first
        var localClass = LocalScopedClasses.FirstOrDefault(t => 
            string.Equals(t.Item1.Name, className, StringComparison.OrdinalIgnoreCase));

        if (localClass is not null)
        {
            return localClass.Item1;
        }

        // Try exported/imported symbols
        if (InternalSymbols.TryGetValue(className, out var symbol) && symbol is ScrClass scrClass)
        {
            return scrClass;
        }

        return null;
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
