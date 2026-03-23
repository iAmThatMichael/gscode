using System;
using System.Collections.Generic;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA.Logic.Components;
using Xunit;

namespace GSCode.Tests;

public class SymbolTableTests
{
    [Fact]
    public void TryGetNamespacedFunctionSymbol_IsCaseInsensitive()
    {
        Dictionary<string, IExportedSymbol> exported = new(StringComparer.OrdinalIgnoreCase);
        ScrFunction function = new()
        {
            Name = "DoThing",
            Namespace = "CustomNamespace",
            Overloads = [new ScrFunctionOverload()]
        };

        exported.Add("CustomNamespace::DoThing", function);
        exported.Add("DoThing", function);

        HashSet<string> knownNamespaces = new(StringComparer.OrdinalIgnoreCase) { "CustomNamespace" };
        SymbolTable table = new(exported, new Dictionary<string, ScrVariable>(StringComparer.OrdinalIgnoreCase), 0, knownNamespaces: knownNamespaces);

        ScrData result = table.TryGetNamespacedFunctionSymbol("customnamespace", "dothing", out SymbolFlags flags, out bool namespaceExists);

        Assert.True(result.HasType(ScrDataTypes.Function));
        Assert.True(flags.HasFlag(SymbolFlags.Global));
        Assert.True(namespaceExists);
    }

    [Fact]
    public void TryGetNamespacedFunctionSymbol_SysNamespace_ReturnsUndefined_WhenNotFound()
    {
        Dictionary<string, IExportedSymbol> exported = new(StringComparer.OrdinalIgnoreCase);
        SymbolTable table = new(exported, new Dictionary<string, ScrVariable>(StringComparer.OrdinalIgnoreCase), 0);

        ScrData result = table.TryGetNamespacedFunctionSymbol("sys", "nonexistent", out SymbolFlags flags, out bool namespaceExists);

        Assert.True(namespaceExists); // sys always exists
        Assert.True(result.HasType(ScrDataTypes.Undefined));
    }

    [Fact]
    public void TryGetNamespacedFunctionSymbol_UnknownNamespace()
    {
        Dictionary<string, IExportedSymbol> exported = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownNamespaces = new(StringComparer.OrdinalIgnoreCase);
        SymbolTable table = new(exported, new Dictionary<string, ScrVariable>(StringComparer.OrdinalIgnoreCase), 0, knownNamespaces: knownNamespaces);

        ScrData result = table.TryGetNamespacedFunctionSymbol("unknown", "func", out SymbolFlags flags, out bool namespaceExists);

        Assert.False(namespaceExists);
    }
}
