using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Reflection;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Serializes test classes that load synthetic API libraries into
/// <see cref="ScriptAnalyserData"/>'s shared static state, so parallel classes
/// don't overwrite each other's library mid-test.
/// </summary>
[CollectionDefinition("ApiLibrary")]
public class ApiLibraryCollection;

/// <summary>
/// The "prefer boolean keyword over integer literal" hint must only fire for parameters
/// declared <c>bool</c>. <see cref="ScrDataTypes.Int"/> (and <see cref="ScrDataTypes.Number"/>)
/// contain the Bool flag bit, so a subset test like <c>HasType(Bool)</c> wrongly matched
/// int parameters and flagged legitimate literal 0/1 arguments.
/// </summary>
[Collection("ApiLibrary")]
public class PreferBooleanLiteralTests
{
    // Revision 1 (deliberately low): after these tests evict and reload the gsc library,
    // other API-loading tests with higher revisions must still be able to load over it.
    private const string ApiJson = """
    {
      "languageId": "gsc",
      "gameId": "t7",
      "revision": 1,
      "api": [
        {
          "name": "TestIntParam",
          "description": "test built-in with an int parameter",
          "overloads": [
            {
              "calledOn": null,
              "parameters": [
                { "name": "value", "description": null, "mandatory": true, "type": { "dataType": "int", "isArray": false } }
              ],
              "returns": { "name": "", "description": null, "type": null, "void": true }
            }
          ],
          "flags": []
        },
        {
          "name": "TestNumberParam",
          "description": "test built-in with a number parameter",
          "overloads": [
            {
              "calledOn": null,
              "parameters": [
                { "name": "value", "description": null, "mandatory": true, "type": { "dataType": "number", "isArray": false } }
              ],
              "returns": { "name": "", "description": null, "type": null, "void": true }
            }
          ],
          "flags": []
        },
        {
          "name": "TestBoolParam",
          "description": "test built-in with a bool parameter",
          "overloads": [
            {
              "calledOn": null,
              "parameters": [
                { "name": "value", "description": null, "mandatory": true, "type": { "dataType": "bool", "isArray": false } }
              ],
              "returns": { "name": "", "description": null, "type": null, "void": true }
            }
          ],
          "flags": []
        },
        {
          "name": "TestOptionalBoolParam",
          "description": "test built-in with an optional bool parameter",
          "overloads": [
            {
              "calledOn": null,
              "parameters": [
                { "name": "value", "description": null, "mandatory": false, "type": { "dataType": "bool", "isArray": false } }
              ],
              "returns": { "name": "", "description": null, "type": null, "void": true }
            }
          ],
          "flags": []
        }
      ]
    }
    """;

    private static async Task<IReadOnlyList<Diagnostic>> AnalyseAsync(string source)
    {
        // Evict any previously loaded gsc library first: LoadLanguageApiData rejects loads
        // whose revision isn't strictly newer than the current one, so repeated test loads
        // (and libraries loaded by other tests) would otherwise be silently skipped.
        var languageLibraries = (System.Collections.IDictionary)typeof(ScriptAnalyserData)
            .GetField("_languageLibraries", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        languageLibraries.Remove("gsc");

        MethodInfo loadLanguageApiData = typeof(ScriptAnalyserData).GetMethod(
            "LoadLanguageApiData",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.True((bool)loadLanguageApiData.Invoke(null, [ApiJson])!);

        Script script = new(new Uri("file:///test.gsc"), ScriptLanguage.Gsc);
        await script.ParseAsync(source);
        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());
        return await script.GetDiagnosticsAsync(CancellationToken.None);
    }

    private static bool IsPreferBooleanLiteral(Diagnostic d)
        => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.PreferBooleanLiteral);

    [Theory]
    [InlineData("TestIntParam(0);")]
    [InlineData("TestIntParam(1);")]
    [InlineData("TestNumberParam(0);")]
    [InlineData("TestNumberParam(1);")]
    public async Task IntOrNumberParameter_LiteralZeroOrOne_DoesNotHint(string call)
    {
        var diagnostics = await AnalyseAsync($$"""
            function test()
            {
                {{call}}
            }
            """);

        Assert.DoesNotContain(diagnostics, IsPreferBooleanLiteral);
    }

    [Theory]
    [InlineData("TestBoolParam(0);")]
    [InlineData("TestBoolParam(1);")]
    [InlineData("TestOptionalBoolParam(0);")]
    public async Task BoolParameter_LiteralZeroOrOne_Hints(string call)
    {
        var diagnostics = await AnalyseAsync($$"""
            function test()
            {
                {{call}}
            }
            """);

        Assert.Contains(diagnostics, IsPreferBooleanLiteral);
    }

    [Fact]
    public async Task BoolParameter_BooleanKeyword_DoesNotHint()
    {
        var diagnostics = await AnalyseAsync("""
            function test()
            {
                TestBoolParam(true);
                TestBoolParam(false);
            }
            """);

        Assert.DoesNotContain(diagnostics, IsPreferBooleanLiteral);
    }

    [Fact]
    public void IsExactly_IntDoesNotSatisfyBool_ButBoolDoes()
    {
        Assert.False(new ScrData(ScrDataTypes.Int).IsExactly(ScrDataTypes.Bool));
        Assert.False(new ScrData(ScrDataTypes.Number).IsExactly(ScrDataTypes.Bool));
        Assert.True(new ScrData(ScrDataTypes.Bool).IsExactly(ScrDataTypes.Bool));
        // Optional parameters union Undefined into the expected type — still exactly bool
        Assert.True(new ScrData(ScrDataTypes.Bool | ScrDataTypes.Undefined).IsExactly(ScrDataTypes.Bool));
        // Sanity: the subset test that caused the false positives
        Assert.True(new ScrData(ScrDataTypes.Int).HasType(ScrDataTypes.Bool));
    }
}

