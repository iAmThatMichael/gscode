using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;
using OmniSharp.Extensions.LanguageServer.Protocol;
using System.Reflection;
using Xunit;

namespace GSCode.Tests;

public class ScrDataApiTypeTests
{
    [Fact]
    public void FromApiType_MapsUint64_ToUInt64()
    {
        ScrFunctionDataType apiType = new()
        {
            DataType = "uint64"
        };

        ScrData result = ScrData.FromApiType(apiType);

        Assert.True(result.HasType(ScrDataTypes.UInt64));
        Assert.Equal(ScrDataTypeNames.UInt64, result.TypeToString());
    }

    [Fact]
    public void GetFunctionReturnType_BuiltInReturn_IsIndeterminate()
    {
        ScrFunction function = new()
        {
            Name = "GetThing",
            Overloads =
            [
                new ScrFunctionOverload
                {
                    Returns = new ScrFunctionReturn
                    {
                        Name = "value",
                        Type = new ScrFunctionDataType
                        {
                            DataType = "int"
                        }
                    }
                }
            ]
        };

        ScrData result = TypeFlowAnalyser.GetFunctionReturnType(function, SymbolFlags.BuiltIn);

        Assert.True(result.HasType(ScrDataTypes.Int));
        Assert.True(result.Indeterminate);
    }

    [Fact]
    public void GetFunctionReturnType_NonBuiltInReturn_IsDeterministic()
    {
        ScrFunction function = new()
        {
            Name = "GetThing",
            Overloads =
            [
                new ScrFunctionOverload
                {
                    Returns = new ScrFunctionReturn
                    {
                        Name = "value",
                        Type = new ScrFunctionDataType
                        {
                            DataType = "int"
                        }
                    }
                }
            ]
        };

        ScrData result = TypeFlowAnalyser.GetFunctionReturnType(function);

        Assert.True(result.HasType(ScrDataTypes.Int));
        Assert.False(result.Indeterminate);
    }

    [Fact]
    public async Task BuiltInUnionReturn_ComparedAfterIsDefined_DoesNotDiagnostic()
    {
        const string apiJson = """
        {
          "languageId": "gsc",
          "gameId": "t7",
          "revision": 999999,
          "api": [
            {
              "name": "GetDStat",
              "description": "test built-in",
              "overloads": [
                {
                  "calledOn": null,
                  "parameters": [
                    {
                      "name": "localClientNum",
                      "description": null,
                      "mandatory": true,
                      "type": { "dataType": "int", "isArray": false }
                    },
                    {
                      "name": "statName",
                      "description": null,
                      "mandatory": true,
                      "type": { "dataType": "string", "isArray": false }
                    }
                  ],
                  "returns": {
                    "name": "value",
                    "description": null,
                    "type": { "dataType": "int | string", "isArray": false },
                    "void": false
                  }
                }
              ],
              "flags": [ "processed" ]
            }
          ]
        }
        """;

        MethodInfo loadLanguageApiData = typeof(ScriptAnalyserData).GetMethod(
            "LoadLanguageApiData",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)loadLanguageApiData.Invoke(null, [apiJson])!);

        Script script = new(new DocumentUri("", "", "", "", ""), "gsc");
        await script.ParseAsync("""
            function test(localClientNum)
            {
                mapIndex = GetDStat(localClientNum, "highestMapReached");
                if (isDefined(mapIndex) && mapIndex < 1)
                {
                }
            }
            """);

        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());

        Assert.DoesNotContain(script.Sense.Diagnostics,
            diagnostic => Equals(diagnostic.Code, (int)GSCErrorCodes.OperatorNotSupportedOnTypes));
    }

    [Fact]
    public async Task BuiltInUnionReturn_ComparedAfterIsDefined_DoesNotDiagnostic_ForActualCscApi()
    {
        string apiPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "GSCode.NET", "api", "t7_api_csc.json"));
        string apiJson = await File.ReadAllTextAsync(apiPath);

        MethodInfo loadLanguageApiData = typeof(ScriptAnalyserData).GetMethod(
            "LoadLanguageApiData",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.True((bool)loadLanguageApiData.Invoke(null, [apiJson])!);

        Script script = new(new DocumentUri("", "", "", "", ""), "csc");
        await script.ParseAsync("""
            function test(localClientNum)
            {
                mapIndex = GetDStat(localClientNum, "highestMapReached");
                if (isDefined(mapIndex) && mapIndex < 1)
                {
                }
            }
            """);

        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());

        Assert.DoesNotContain(script.Sense.Diagnostics,
            diagnostic => Equals(diagnostic.Code, (int)GSCErrorCodes.OperatorNotSupportedOnTypes));
    }
}
