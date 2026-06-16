using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression tests for namespace usage and missing-import detection (issue #71).
/// A namespaced call must be validated against the script's direct #using dependencies:
/// an unimported namespace is an error even if it exists elsewhere in the workspace.
/// </summary>
public class NamespaceDiagnosticsTests
{
    internal static bool HasCode(Diagnostic diagnostic, GSCErrorCodes code)
        => diagnostic.Code.HasValue
           && diagnostic.Code.Value.IsLong
           && diagnostic.Code.Value.Long == (long)code;

    private static ScrFunction MakeFunction(string ns, string name) => new()
    {
        Name = name,
        Namespace = ns,
        Overloads = [new ScrFunctionOverload()]
    };

    private static async Task<(Script Script, List<Diagnostic> Diagnostics)> AnalyseAsync(
        string source,
        IEnumerable<IExportedSymbol>? dependencyExports = null,
        IEnumerable<(string Namespace, string Name)>? dependencyLocations = null)
    {
        Script script = new(new Uri("file:///scripts/zm/test.gsc"), ScriptLanguage.Gsc);
        await script.ParseAsync(source);

        // Mirror ScriptManager's dependency merge: locations of symbols defined in direct
        // #using dependencies are added to the editor script's DefinitionsTable, which is
        // where KnownNamespaces comes from.
        if (dependencyLocations is not null && script.DefinitionsTable is not null)
        {
            foreach (var (ns, name) in dependencyLocations)
            {
                script.DefinitionsTable.AddFunctionLocation(ns, name, "scripts\\zm\\dep.gsc", default(TokenRange));
            }
        }

        await script.AnalyseAsync(dependencyExports ?? []);
        var diagnostics = await script.GetDiagnosticsAsync(CancellationToken.None);
        return (script, diagnostics);
    }

    [Theory]
    [InlineData("""
        function test()
        {
            zombie_utility::is_player_valid(self);
        }
        """)]
    [InlineData("""
        function test()
        {
            if (isplayer(self) && zombie_utility::is_player_valid(self))
            {
            }
        }
        """)]
    [InlineData("""
        function test()
        {
            self thread zombie_utility::is_player_valid(self);
        }
        """)]
    [InlineData("""
        function test()
        {
            cb = &zombie_utility::is_player_valid;
            [[cb]]();
        }
        """)]
    public async Task UnimportedNamespace_EmitsUnknownNamespace(string source)
    {
        var (_, diagnostics) = await AnalyseAsync(source);

        Assert.Contains(diagnostics, d => HasCode(d, GSCErrorCodes.UnknownNamespace));
    }

    [Fact]
    public async Task ImportedNamespaceFunction_ResolvesWithoutDiagnostics()
    {
        var (_, diagnostics) = await AnalyseAsync("""
            function test()
            {
                zombie_utility::is_player_valid(self);
            }
            """,
            dependencyExports: [MakeFunction("zombie_utility", "is_player_valid")],
            dependencyLocations: [("zombie_utility", "is_player_valid")]);

        Assert.DoesNotContain(diagnostics, d => HasCode(d, GSCErrorCodes.UnknownNamespace));
        Assert.DoesNotContain(diagnostics, d => HasCode(d, GSCErrorCodes.NamespaceDoesNotContainFunction));
    }

    [Fact]
    public async Task ImportedNamespace_MissingFunction_EmitsNamespaceDoesNotContainFunction()
    {
        var (_, diagnostics) = await AnalyseAsync("""
            function test()
            {
                zombie_utility::not_a_real_function(self);
            }
            """,
            dependencyExports: [MakeFunction("zombie_utility", "is_player_valid")],
            dependencyLocations: [("zombie_utility", "is_player_valid")]);

        Assert.DoesNotContain(diagnostics, d => HasCode(d, GSCErrorCodes.UnknownNamespace));
        Assert.Contains(diagnostics, d => HasCode(d, GSCErrorCodes.NamespaceDoesNotContainFunction));
    }

    [Fact]
    public async Task DefaultNamespace_IsScriptFileName()
    {
        // No #namespace directive: the VM uses the script's file name as its namespace,
        // so test::foo() must resolve within scripts/zm/test.gsc.
        var (script, diagnostics) = await AnalyseAsync("""
            function foo()
            {
            }

            function bar()
            {
                test::foo();
            }
            """);

        Assert.NotNull(script.DefinitionsTable);
        Assert.Contains(script.DefinitionsTable!.ExportedFunctions,
            f => f.Name == "foo" && string.Equals(f.Namespace, "test", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(diagnostics, d => HasCode(d, GSCErrorCodes.UnknownNamespace));
        Assert.DoesNotContain(diagnostics, d => HasCode(d, GSCErrorCodes.NamespaceDoesNotContainFunction));
    }

    [Fact]
    public async Task MultipleNamespacesPerFile_FunctionsExportUnderTheirOwnNamespace()
    {
        var (script, diagnostics) = await AnalyseAsync("""
            #namespace first_ns;

            function in_first()
            {
            }

            #namespace second_ns;

            function in_second()
            {
                first_ns::in_first();
                second_ns::in_second();
            }
            """);

        Assert.NotNull(script.DefinitionsTable);
        var exports = script.DefinitionsTable!.ExportedFunctions;
        Assert.Contains(exports, f => f.Name == "in_first" && string.Equals(f.Namespace, "first_ns", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(exports, f => f.Name == "in_second" && string.Equals(f.Namespace, "second_ns", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(diagnostics, d => HasCode(d, GSCErrorCodes.UnknownNamespace));
        Assert.DoesNotContain(diagnostics, d => HasCode(d, GSCErrorCodes.NamespaceDoesNotContainFunction));
    }
}

