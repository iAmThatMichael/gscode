using GSCode.Data.Models.Interfaces;
using GSCode.NET.LSP;
using Xunit;

namespace GSCode.Tests;

public class GlobalSymbolRegistryTests
{
    private static SymbolDefinition Function(string ns, string name, string filePath)
        => new(ns, name, ExportedSymbolType.Function, filePath, default);

    [Fact]
    public void GetSymbolsDefinedInFile_ReturnsOwnSubmissions_EvenWhenShadowed()
    {
        var registry = new GlobalSymbolRegistry();

        registry.UpdateSymbolsForFile(@"C:\a\scripts\zm\util.gsc", [Function("util", "do_thing", @"C:\a\scripts\zm\util.gsc")]);
        registry.UpdateSymbolsForFile(@"C:\b\scripts\zm\util.gsc", [Function("util", "do_thing", @"C:\b\scripts\zm\util.gsc")]);

        var fromA = registry.GetSymbolsDefinedInFile(@"C:\a\scripts\zm\util.gsc");
        var fromB = registry.GetSymbolsDefinedInFile(@"C:\b\scripts\zm\util.gsc");

        Assert.Single(fromA);
        Assert.Equal(@"C:\a\scripts\zm\util.gsc", fromA[0].FilePath);
        Assert.Single(fromB);
        Assert.Equal(@"C:\b\scripts\zm\util.gsc", fromB[0].FilePath);
    }

    [Fact]
    public void GetSymbolsDefinedInFile_UnknownFile_ReturnsEmpty()
    {
        var registry = new GlobalSymbolRegistry();
        Assert.Empty(registry.GetSymbolsDefinedInFile(@"C:\nowhere\scripts\x.gsc"));
    }

    [Fact]
    public void GetAllNamespaces_FiltersByLanguage()
    {
        var registry = new GlobalSymbolRegistry();
        registry.UpdateSymbolsForFile(@"C:\r\scripts\shared\a.gsc", [Function("server_ns", "f", @"C:\r\scripts\shared\a.gsc")]);
        registry.UpdateSymbolsForFile(@"C:\r\scripts\shared\b.csc", [Function("client_ns", "g", @"C:\r\scripts\shared\b.csc")]);

        var gsc = registry.GetAllNamespaces("gsc");
        var csc = registry.GetAllNamespaces("csc");

        Assert.Contains("server_ns", gsc);
        Assert.DoesNotContain("client_ns", gsc);
        Assert.Contains("client_ns", csc);
        Assert.DoesNotContain("server_ns", csc);
    }

    [Fact]
    public void GetFunctionsInNamespace_AggregatesAcrossFiles()
    {
        var registry = new GlobalSymbolRegistry();
        registry.UpdateSymbolsForFile(@"C:\r\scripts\zm\zu_a.gsc", [Function("zombie_utility", "func_a", @"C:\r\scripts\zm\zu_a.gsc")]);
        registry.UpdateSymbolsForFile(@"C:\r\scripts\zm\zu_b.gsc", [Function("zombie_utility", "func_b", @"C:\r\scripts\zm\zu_b.gsc")]);

        var functions = registry.GetFunctionsInNamespace("zombie_utility", "gsc");

        Assert.Equal(2, functions.Count);
        Assert.Contains(functions, f => f.Name == "func_a" && f.FilePath.EndsWith("zu_a.gsc"));
        Assert.Contains(functions, f => f.Name == "func_b" && f.FilePath.EndsWith("zu_b.gsc"));
    }

    [Fact]
    public void FindFilesForNamespacedFunction_ReturnsAllDefiningFiles()
    {
        var registry = new GlobalSymbolRegistry();
        registry.UpdateSymbolsForFile(@"C:\a\scripts\zm\util.gsc", [Function("util", "do_thing", @"C:\a\scripts\zm\util.gsc")]);
        registry.UpdateSymbolsForFile(@"C:\b\scripts\zm\util.gsc", [Function("util", "do_thing", @"C:\b\scripts\zm\util.gsc")]);

        var files = registry.FindFilesForNamespacedFunction("util", "do_thing");

        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void FindFilesForNamespace_ReturnsDefiningFiles_CaseInsensitive()
    {
        var registry = new GlobalSymbolRegistry();
        registry.UpdateSymbolsForFile(@"C:\r\scripts\shared\ai\zombie_utility.gsc",
            [Function("zombie_utility", "is_player_valid", @"C:\r\scripts\shared\ai\zombie_utility.gsc")]);

        var files = registry.FindFilesForNamespace("Zombie_Utility");

        Assert.Single(files);
        Assert.EndsWith("zombie_utility.gsc", files[0]);
    }

    [Fact]
    public void RemoveSymbolsFromFile_RemovesNamespaceContribution()
    {
        var registry = new GlobalSymbolRegistry();
        registry.UpdateSymbolsForFile(@"C:\r\scripts\zm\a.gsc", [Function("my_ns", "f", @"C:\r\scripts\zm\a.gsc")]);

        registry.RemoveSymbolsFromFile(@"C:\r\scripts\zm\a.gsc");

        Assert.Empty(registry.GetSymbolsDefinedInFile(@"C:\r\scripts\zm\a.gsc"));
        Assert.DoesNotContain("my_ns", registry.GetAllNamespaces("gsc"));
    }

    [Fact]
    public void GlobalSymbolRegistry_Dispose_DoesNotThrow()
    {
        var registry = new GlobalSymbolRegistry();
        registry.UpdateSymbolsForFile(@"C:\a\x.gsc",
            [new SymbolDefinition("ns", "func", ExportedSymbolType.Function, @"C:\a\x.gsc", default)]);

        var ex = Record.Exception(() => registry.Dispose());
        Assert.Null(ex);
    }
}
