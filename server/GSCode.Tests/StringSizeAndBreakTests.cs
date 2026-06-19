using GSCode.Data;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.DFA;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Strings expose a readonly <c>size</c> property (the string's length), like arrays.
/// </summary>
public class StringSizeFieldTests
{
    private static async Task<IReadOnlyList<Diagnostic>> AnalyseAsync(string source)
    {
        Script script = new(new Uri("file:///string_size_test.gsc"), ScriptLanguage.Gsc);
        await script.ParseAsync(source);
        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());
        return await script.GetDiagnosticsAsync(CancellationToken.None);
    }

    [Fact]
    public void TryGetField_SizeOnString_IsReadOnlyInt()
    {
        ScrData result = new ScrData(ScrDataTypes.String).TryGetField("size", out ScrDataTypes? incompatible);

        Assert.Null(incompatible);
        Assert.True(result.IsExactly(ScrDataTypes.Int));
        Assert.True(result.ReadOnly);
    }

    [Fact]
    public void TryGetField_SizeOnIString_IsReadOnlyInt()
    {
        ScrData result = new ScrData(ScrDataTypes.IString).TryGetField("size", out ScrDataTypes? incompatible);

        Assert.Null(incompatible);
        Assert.True(result.IsExactly(ScrDataTypes.Int));
        Assert.True(result.ReadOnly);
    }

    [Fact]
    public void TryGetField_OtherFieldOnString_IsIncompatible()
    {
        new ScrData(ScrDataTypes.String).TryGetField("length", out ScrDataTypes? incompatible);

        Assert.NotNull(incompatible);
    }

    [Fact]
    public void TrySetField_SizeOnString_FailsAsReadOnly()
    {
        bool success = new ScrData(ScrDataTypes.String).TrySetField("size", new ScrData(ScrDataTypes.Int), out ScrSetFieldFailure? failure);

        Assert.False(success);
        Assert.NotNull(failure);
        Assert.True(failure.Value.SizeFieldReadOnly);
    }

    [Fact]
    public async Task ReadingStringSize_ProducesNoDiagnostics()
    {
        var diagnostics = await AnalyseAsync("""
            function test()
            {
                s = "hello";
                x = s.size;
                return x;
            }
            """);

        Assert.DoesNotContain(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.DoesNotContainMember));
    }

    [Fact]
    public async Task AssigningStringSize_ReportsReadOnly()
    {
        var diagnostics = await AnalyseAsync("""
            function test()
            {
                s = "hello";
                s.size = 3;
            }
            """);

        Assert.Contains(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.CannotAssignToReadOnlyProperty));
    }
}

/// <summary>
/// Arrays built up via indexed assignments (arr[0] = x; arr[1] = y;) must have their
/// <c>size</c> field accessible without a false DoesNotContainMember diagnostic.
/// </summary>
public class ArrayIndexedAssignmentTests
{
    private static async Task<IReadOnlyList<Diagnostic>> AnalyseAsync(string source)
    {
        Script script = new(new Uri("file:///array_indexed_test.gsc"), ScriptLanguage.Gsc);
        await script.ParseAsync(source);
        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());
        return await script.GetDiagnosticsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ArrayBuiltViaIndexedAssignment_SizeAccessProducesNoDiagnostic()
    {
        var diagnostics = await AnalyseAsync("""
            function test()
            {
                refs[0] = "guts";
                refs[1] = "right_arm";
                refs[2] = "left_arm";
                x = refs[randomint(refs.size)];
            }
            """);

        Assert.DoesNotContain(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.DoesNotContainMember));
    }
}

/// <summary>
/// A <c>break</c> outside of any loop or switch is a runtime no-op in GSC. It must parse
/// without syntax errors, flag an "unnecessary" diagnostic, and not divert control flow
/// (statements after it are still reachable and analysed).
/// </summary>
public class BreakNoOpTests
{
    private static async Task<IReadOnlyList<Diagnostic>> AnalyseAsync(string source)
    {
        Script script = new(new Uri("file:///break_noop_test.gsc"), ScriptLanguage.Gsc);
        await script.ParseAsync(source);
        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());
        return await script.GetDiagnosticsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BreakInFunctionBody_FlagsNoEffect_WithoutSyntaxErrors()
    {
        var diagnostics = await AnalyseAsync("""
            function test()
            {
                break;
            }
            """);

        Assert.Contains(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.BreakHasNoEffect));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task BreakInIfWithoutLoop_FlagsNoEffect()
    {
        var diagnostics = await AnalyseAsync("""
            function test(a)
            {
                if (isdefined(a))
                {
                    break;
                }
            }
            """);

        Assert.Contains(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.BreakHasNoEffect));
    }

    [Fact]
    public async Task BreakInLoop_DoesNotFlag()
    {
        var diagnostics = await AnalyseAsync("""
            function test(items)
            {
                foreach (item in items)
                {
                    break;
                }
                for (i = 0; i < 3; i++)
                {
                    break;
                }
                while (isdefined(items))
                {
                    break;
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.BreakHasNoEffect));
    }

    [Fact]
    public async Task BreakInSwitch_DoesNotFlag()
    {
        var diagnostics = await AnalyseAsync("""
            function test(a)
            {
                switch (a)
                {
                    case 1:
                        break;
                    default:
                        break;
                }
            }
            """);

        Assert.DoesNotContain(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.BreakHasNoEffect));
    }

    [Fact]
    public async Task StatementsAfterNoOpBreak_AreStillAnalysed()
    {
        // The no-op break must not divert control flow: the readonly-assignment error
        // after it proves the following statements are still reachable and analysed.
        var diagnostics = await AnalyseAsync("""
            function test()
            {
                s = "hello";
                break;
                s.size = 3;
            }
            """);

        Assert.Contains(diagnostics, d => NamespaceDiagnosticsTests.HasCode(d, GSCErrorCodes.CannotAssignToReadOnlyProperty));
    }
}

