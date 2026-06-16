using GSCode.Data;
using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.DFA;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression tests for type flow analysis convergence. The worklist's convergence checks
/// rest on <see cref="ScrData"/> value equality: the compiler-synthesised record equality
/// compared <see cref="ScrData.SubTypes"/> by reference, so any value carrying sub-types
/// (entity narrowings, function pointers, class instances) flowing through a CFG cycle was
/// reported as "changed" on every pass and analysis ran until the iteration cap.
/// </summary>
public class TypeFlowConvergenceTests
{
    private static ScrData PlayerEntity()
        => new(ScrDataTypes.Entity, [new ScrDataEntityType { EntityType = ScrEntityTypes.Player }]);

    [Fact]
    public void Merge_WithSubTypes_ProducesValueEqualResults()
    {
        // Each Merge builds a fresh SubTypes set instance; the results must still be equal.
        ScrData m1 = ScrData.Merge(PlayerEntity(), ScrData.Undefined());
        ScrData m2 = ScrData.Merge(PlayerEntity(), ScrData.Undefined());

        Assert.Equal(m1, m2);
        Assert.Equal(m1.GetHashCode(), m2.GetHashCode());
    }

    [Fact]
    public void Equality_DifferentSubTypes_AreNotEqual()
    {
        ScrData vehicle = new(ScrDataTypes.Entity, [new ScrDataEntityType { EntityType = ScrEntityTypes.Vehicle }]);

        Assert.NotEqual(PlayerEntity(), vehicle);
    }

    [Fact]
    public void Equality_NullAndEmptySubTypes_AreEqual()
    {
        ScrData withEmpty = new(ScrDataTypes.Entity, Array.Empty<IScrDataSubType>(), booleanValue: true);
        ScrData withNull = new(ScrDataTypes.Entity);

        Assert.Equal(withNull, withEmpty);
        Assert.Equal(withNull.GetHashCode(), withEmpty.GetHashCode());
    }

    [Fact]
    public void Equality_IgnoresFieldName()
    {
        ScrData a = new(ScrDataTypes.Int);
        ScrData b = new(ScrDataTypes.Int);
        b.FieldName = "size";

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Merge_DisagreeingTruthiness_IsUnknown()
    {
        ScrData truthy = new(ScrDataTypes.Bool, booleanValue: true);
        ScrData falsy = new(ScrDataTypes.Bool, booleanValue: false);

        Assert.Null(ScrData.Merge(truthy, falsy).IsTruthy());
    }

    [Fact]
    public void Merge_UnknownTruthiness_StaysUnknown()
    {
        // The accumulator previously started at true, so merging two unknown-truthiness
        // values produced "definitely truthy".
        Assert.Null(ScrData.Merge(new ScrData(ScrDataTypes.Int), new ScrData(ScrDataTypes.Int)).IsTruthy());
    }

    [Fact]
    public void Merge_AgreeingTruthiness_IsPreserved()
    {
        ScrData truthy1 = new(ScrDataTypes.Bool, booleanValue: true);
        ScrData truthy2 = new(ScrDataTypes.Bool, booleanValue: true);

        Assert.True(ScrData.Merge(truthy1, truthy2).IsTruthy());
        Assert.False(ScrData.Merge(ScrData.Undefined(), ScrData.Undefined()).IsTruthy());
    }

    [Fact]
    public void Widen_DiscardsValueFacts_AndUnionsTypes()
    {
        ScrData widened = TypeFlowAnalyser.Widen(PlayerEntity(), new ScrData(ScrDataTypes.Int));

        Assert.Equal(ScrDataTypes.Entity | ScrDataTypes.Int, widened.Type);
        Assert.Null(widened.SubTypes);
        Assert.Null(widened.IsTruthy());
        Assert.True(widened.Indeterminate);
        // Widening with no new information is a fixed point — required for termination.
        Assert.Equal(widened, TypeFlowAnalyser.Widen(widened, widened));
    }

    [Fact]
    public async Task LoopWithSubTypedValues_ConvergesWithinIterationLimit()
    {
        // Function pointers and isplayer() narrowing both attach SubTypes, and both sit
        // inside nested loops — the exact shape that previously never converged.
        Script script = new(new Uri("file:///convergence_test.gsc"), ScriptLanguage.Gsc);
        await script.ParseAsync("""
            function callback() {}

            function test(players)
            {
                f = &callback;
                while (isdefined(players))
                {
                    foreach (p in players)
                    {
                        if (isplayer(p))
                        {
                            f = &callback;
                        }
                    }
                }
            }
            """);

        await script.AnalyseAsync(Array.Empty<IExportedSymbol>());

        Assert.Equal(0, script.Sense.TypeFlowIterationLimitHits);
    }
}
