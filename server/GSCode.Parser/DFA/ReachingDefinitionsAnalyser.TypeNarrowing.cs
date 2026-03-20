using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;

namespace GSCode.Parser.DFA;

internal ref partial struct ReachingDefinitionsAnalyser
{
    private static bool IsLogicalBinaryOperator(TokenType op) => op is TokenType.And or TokenType.Or;

    /// <summary>
    /// Describes how an expression being true/false narrows types.
    /// We keep this separate from <see cref="ScrData"/> so we don't have to refactor all expression analysis at once.
    /// </summary>
    private readonly record struct ConditionResult(
        ScrData Value,
        Dictionary<string, TypeNarrowing> WhenTrue,
        Dictionary<string, TypeNarrowing> WhenFalse);

    private ScrData AnalyseLogicalBinaryExpr(BinaryExprNode binary, SymbolTable symbolTable)
    {
        // Logical operators are treated as condition expressions.
        // This enables short-circuit-aware narrowing for RHS analysis without having to refactor
        // AnalyseExpr(...) to return facts for all expressions.
        return AnalyseCondition(binary, symbolTable).Value;
    }

    private ConditionResult AnalyseCondition(ExprNode expr, SymbolTable symbolTable)
    {
        switch (expr)
        {
            case PrefixExprNode prefix when prefix.Operation == TokenType.Not:
                {
                    ConditionResult operand = AnalyseCondition(prefix.Operand, symbolTable);
                    bool? truthy = operand.Value.IsTruthy();

                    ScrData notValue = truthy is null
                        ? new ScrData(ScrDataTypes.Bool)
                        : new ScrData(ScrDataTypes.Bool, !truthy.Value);

                    return new ConditionResult(
                        notValue,
                        WhenTrue: operand.WhenFalse,
                        WhenFalse: operand.WhenTrue);
                }

            case BinaryExprNode binary when binary.Operation == TokenType.And:
                return AnalyseConditionAnd(binary, symbolTable);

            case BinaryExprNode binary when binary.Operation == TokenType.Or:
                return AnalyseConditionOr(binary, symbolTable);

            // Free call: IsDefined(x), IsPlayer(ent), etc.
            case FunCallNode call when TryEmulateCondition(call, null, symbolTable, out ConditionResult freeResult):
                return freeResult;

            // Method call: self IsPlayer()
            case CalledOnNode { Call: FunCallNode innerCall, On: var targetExpr }
                when TryEmulateCondition(innerCall, targetExpr, symbolTable, out ConditionResult methodResult):
                return methodResult;

            default:
                {
                    // Not a condition we can infer from yet. Still analyze it normally.
                    ScrData value = AnalyseExpr(expr, symbolTable, Sense);
                    return new ConditionResult(
                        value,
                        WhenTrue: CreateEmptyNarrowings(),
                        WhenFalse: CreateEmptyNarrowings());
                }
        }
    }

    private ConditionResult AnalyseConditionAnd(BinaryExprNode andExpr, SymbolTable symbolTable)
    {
        ConditionResult left = AnalyseCondition(andExpr.Left!, symbolTable);

        // Short-circuit: RHS is only evaluated if LHS is true.
        SymbolTable rhsTable = CreateRefinedSymbolTable(symbolTable, left.WhenTrue);
        ConditionResult right = AnalyseCondition(andExpr.Right!, rhsTable);

        ScrData value = AnalyseAndOp(andExpr, left.Value, right.Value);

        // Definite facts:
        // - if (A && B) is true => A is true and B is true
        // - if (A && B) is false => ambiguous (could be A false OR A true and B false), so keep empty for now
        Dictionary<string, TypeNarrowing> whenTrue = MergeNarrowings(left.WhenTrue, right.WhenTrue);

        return new ConditionResult(
            value,
            WhenTrue: whenTrue,
            WhenFalse: CreateEmptyNarrowings());
    }

    private ConditionResult AnalyseConditionOr(BinaryExprNode orExpr, SymbolTable symbolTable)
    {
        ConditionResult left = AnalyseCondition(orExpr.Left!, symbolTable);

        // Short-circuit: RHS is only evaluated if LHS is false.
        SymbolTable rhsTable = CreateRefinedSymbolTable(symbolTable, left.WhenFalse);
        ConditionResult right = AnalyseCondition(orExpr.Right!, rhsTable);

        ScrData value = AnalyseOrOp(orExpr, left.Value, right.Value);

        // Definite facts:
        // - if (A || B) is false => A is false and B is false
        // - if (A || B) is true => ambiguous, so keep empty for now
        Dictionary<string, TypeNarrowing> whenFalse = MergeNarrowings(left.WhenFalse, right.WhenFalse);

        return new ConditionResult(
            value,
            WhenTrue: CreateEmptyNarrowings(),
            WhenFalse: whenFalse);
    }

    /// <summary>
    /// Attempts to evaluate a function call as an emulated condition, producing type narrowing facts.
    /// </summary>
    private bool TryEmulateCondition(FunCallNode call, ExprNode? targetExpr, SymbolTable symbolTable, out ConditionResult result)
    {
        result = default;

        // Resolve function name.
        if (call.Function is not IdentifierExprNode identifier)
        {
            return false;
        }

        if (!EmulatedFunctionRegistry.TryGet(identifier.Identifier, out EmulatedFunction? emulated) || emulated.EmulateNarrowing is null)
        {
            return false;
        }

        // Resolve the subject variable name to narrow.
        // For method calls (ent IsPlayer()), the subject is the target expression.
        // For free calls (IsPlayer(ent)), the subject is the first argument.
        string? subjectName = null;
        ScrData? targetData = null;

        if (targetExpr is not null)
        {
            // Called-on syntax: target is the subject
            if (targetExpr is IdentifierExprNode targetId)
            {
                subjectName = targetId.Identifier;
            }
            targetData = AnalyseExpr(targetExpr, symbolTable, Sense);
        }
        else
        {
            // Free call: first argument is the subject
            if (call.Arguments.Arguments.Count > 0 && call.Arguments.Arguments.First?.Value is IdentifierExprNode argId)
            {
                subjectName = argId.Identifier;
            }
        }

        // Analyse arguments for side effects / symbol usage.
        ScrData[] argTypes = AnalyseCallArguments(call, symbolTable);

        EmulationContext ctx = new()
        {
            Call = call,
            ArgumentTypes = argTypes,
            Target = targetData,
            Silent = Silent,
            Sense = Sense
        };

        NarrowingResult? narrowing = emulated.EmulateNarrowing(subjectName, in ctx);
        if (narrowing is null)
        {
            return false;
        }

        result = new ConditionResult(narrowing.Value.Value, narrowing.Value.WhenTrue, narrowing.Value.WhenFalse);
        return true;
    }

    /// <summary>
    /// Analyses all arguments in a function call and returns their types.
    /// </summary>
    private ScrData[] AnalyseCallArguments(FunCallNode call, SymbolTable symbolTable)
    {
        int count = call.Arguments.Arguments.Count;
        ScrData[] types = new ScrData[count];
        int index = 0;

        foreach (ExprNode? argument in call.Arguments.Arguments)
        {
            types[index] = argument is not null
                ? AnalyseExpr(argument, symbolTable, Sense)
                : ScrData.Default;
            index++;
        }

        return types;
    }

    private static Dictionary<string, TypeNarrowing> CreateEmptyNarrowings()
    {
        return new Dictionary<string, TypeNarrowing>(StringComparer.OrdinalIgnoreCase);
    }

    private static TypeNarrowing Compose(TypeNarrowing a, TypeNarrowing b)
    {
        // Compose RequireSubTypes: if both specify constraints, intersect them.
        ImmutableHashSet<IScrDataSubType>? subTypes = (a.RequireSubTypes, b.RequireSubTypes) switch
        {
            (not null, not null) => a.RequireSubTypes.Intersect(b.RequireSubTypes),
            (not null, null) => a.RequireSubTypes,
            (null, not null) => b.RequireSubTypes,
            _ => null
        };

        return new TypeNarrowing(a.KeepMask & b.KeepMask, a.RemoveMask | b.RemoveMask, subTypes);
    }

    private static Dictionary<string, TypeNarrowing> MergeNarrowings(
        Dictionary<string, TypeNarrowing> a,
        Dictionary<string, TypeNarrowing> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return CreateEmptyNarrowings();
        }
        if (a.Count == 0)
        {
            return new Dictionary<string, TypeNarrowing>(b, StringComparer.OrdinalIgnoreCase);
        }
        if (b.Count == 0)
        {
            return new Dictionary<string, TypeNarrowing>(a, StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, TypeNarrowing> merged = new Dictionary<string, TypeNarrowing>(a, StringComparer.OrdinalIgnoreCase);
        foreach ((string key, TypeNarrowing narrowing) in b)
        {
            if (merged.TryGetValue(key, out TypeNarrowing existing))
            {
                merged[key] = Compose(existing, narrowing);
            }
            else
            {
                merged[key] = narrowing;
            }
        }
        return merged;
    }

    private static ScrDataTypes ApplyTypeMask(TypeNarrowing narrowing, ScrDataTypes originalType)
    {
        // Clamp ~ to the valid type universe (Any == all valid bits except Error).
        ScrDataTypes universe = ScrDataTypes.Any;
        ScrDataTypes keep = narrowing.KeepMask & universe;
        ScrDataTypes remove = narrowing.RemoveMask & universe;

        return (originalType & keep) & (universe & ~remove);
    }

    /// <summary>
    /// Applies a type narrowing to an ScrData value, handling both type bitmask and sub-type narrowing.
    /// </summary>
    private static ScrData ApplyNarrowing(TypeNarrowing narrowing, ScrData original)
    {
        ScrDataTypes newType = ApplyTypeMask(narrowing, original.Type);
        ImmutableHashSet<IScrDataSubType>? subTypes = narrowing.RequireSubTypes ?? original.SubTypes;

        // If narrowing pins the type to exactly the KeepMask, the result is deterministic.
        // Otherwise, preserve the original indeterminate status.
        bool indeterminate = original.Indeterminate && (newType != narrowing.KeepMask);

        return new ScrData(newType, subTypes, original.BooleanValue, original.ReadOnly)
            { Indeterminate = indeterminate };
    }

    /// <summary>
    /// Applies narrowings to the given symbol table in-place, mutating existing variable entries.
    /// Used for assert() where narrowings should persist for all subsequent code.
    /// </summary>
    private static void ApplyNarrowingsToSymbolTable(SymbolTable symbolTable, Dictionary<string, TypeNarrowing> narrowings)
    {
        foreach ((string symbol, TypeNarrowing narrowing) in narrowings)
        {
            if (!symbolTable.VariableSymbols.TryGetValue(symbol, out ScrVariable? existing) || existing is null)
            {
                continue;
            }

            symbolTable.VariableSymbols[symbol] = existing with { Data = ApplyNarrowing(narrowing, existing.Data) };
        }
    }

    private SymbolTable CreateRefinedSymbolTable(SymbolTable baseTable, Dictionary<string, TypeNarrowing> narrowings)
    {
        if (narrowings.Count == 0)
        {
            return baseTable;
        }

        Dictionary<string, ScrVariable> refinedEnv = new(baseTable.VariableSymbols, StringComparer.OrdinalIgnoreCase);

        foreach ((string symbol, TypeNarrowing narrowing) in narrowings)
        {
            if (!refinedEnv.TryGetValue(symbol, out ScrVariable? existing) || existing is null)
            {
                continue;
            }

            refinedEnv[symbol] = existing with { Data = ApplyNarrowing(narrowing, existing.Data) };
        }

        return new SymbolTable(
            baseTable.GlobalSymbolTable,
            refinedEnv,
            baseTable.LexicalScope,
            ApiData,
            baseTable.CurrentClass,
            baseTable.CurrentNamespace,
            baseTable.KnownNamespaces);
    }
}
