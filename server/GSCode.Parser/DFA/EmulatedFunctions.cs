using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using GSCode.Data;
using GSCode.Parser.AST;

namespace GSCode.Parser.DFA;

/// <summary>
/// Concrete emulated function definitions for built-in/reserved functions.
/// </summary>
internal static class EmulatedFunctions
{
    public static readonly EmulatedFunction IsDefined = new()
    {
        Name = "isdefined",
        EmulateCall = static (in EmulationContext ctx) =>
        {
            int argCount = ctx.Call.Arguments.Arguments.Count;

            if (argCount < 1)
            {
                ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooFewArguments, "isdefined", argCount, 1);
            }
            else if (argCount > 1)
            {
                ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooManyArguments, "isdefined", argCount, 1);
            }

            return new ScrData(ScrDataTypes.Bool);
        },
        EmulateNarrowing = static (string? subjectName, in EmulationContext ctx) =>
        {
            if (subjectName is null)
            {
                return null;
            }

            Dictionary<string, TypeNarrowing> whenTrue = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, TypeNarrowing> whenFalse = new(StringComparer.OrdinalIgnoreCase);

            // True: subject is not undefined
            whenTrue[subjectName] = new TypeNarrowing(ScrDataTypes.Any, ScrDataTypes.Undefined);
            // False: subject is undefined
            whenFalse[subjectName] = new TypeNarrowing(ScrDataTypes.Undefined, 0);

            return new NarrowingResult(new ScrData(ScrDataTypes.Bool), whenTrue, whenFalse);
        }
    };

    public static readonly EmulatedFunction VectorScale = new()
    {
        Name = "vectorscale",
        EmulateCall = static (in EmulationContext ctx) =>
        {
            int argCount = ctx.Call.Arguments.Arguments.Count;

            if (argCount < 2)
            {
                ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooFewArguments, "vectorscale", argCount, 2);
            }
            else if (argCount > 2)
            {
                ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooManyArguments, "vectorscale", argCount, 2);
            }

            // Validate first argument type (should be Vector)
            if (ctx.ArgumentTypes.Length > 0)
            {
                ScrData vecData = ctx.ArgumentTypes[0];
                if (!vecData.IsCompatibleWith(ScrDataTypes.Vector))
                {
                    ExprNode? firstArg = GetArgument(ctx.Call, 0);
                    if (firstArg is not null)
                    {
                        ctx.AddDiagnostic(firstArg.Range, GSCErrorCodes.NoImplicitConversionExists, vecData.TypeToString(), ScrDataTypeNames.Vector);
                    }
                }
            }

            // Validate second argument type (should be Number)
            if (ctx.ArgumentTypes.Length > 1)
            {
                ScrData scaleData = ctx.ArgumentTypes[1];
                if (!scaleData.IsCompatibleWith(ScrDataTypes.Number))
                {
                    ExprNode? secondArg = GetArgument(ctx.Call, 1);
                    if (secondArg is not null)
                    {
                        ctx.AddDiagnostic(secondArg.Range, GSCErrorCodes.NoImplicitConversionExists, scaleData.TypeToString(), ScrDataTypeNames.Number);
                    }
                }
            }

            return new ScrData(ScrDataTypes.Vector);
        }
    };

    public static readonly EmulatedFunction LuiNotifyEvent = new()
    {
        Name = "luinotifyevent",
        EmulateCall = static (in EmulationContext ctx) =>
        {
            int argCount = ctx.Call.Arguments.Arguments.Count;

            // Must have at least 1 argument (the event name).
            if (argCount < 1)
            {
                ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooFewArguments, "luinotifyevent", argCount, 1);
                return ScrData.Void;
            }

            // If arg2 (parameter count) is provided and is a constant int, validate the remaining arg count.
            if (argCount >= 2)
            {
                ExprNode? paramCountArg = GetArgument(ctx.Call, 1);
                if (paramCountArg is DataExprNode { Type: ScrDataTypes.Int, Value: int declaredParamCount })
                {
                    int expectedTotal = 2 + declaredParamCount;
                    if (argCount < expectedTotal)
                    {
                        ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooFewArguments, "luinotifyevent", argCount, expectedTotal);
                    }
                    else if (argCount > expectedTotal)
                    {
                        ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooManyArguments, "luinotifyevent", argCount, expectedTotal);
                    }
                }
            }

            return ScrData.Void;
        }
    };

    public static readonly EmulatedFunction LuiNotifyEventToSpectators = new()
    {
        Name = "luinotifyeventtospectators",
        EmulateCall = static (in EmulationContext ctx) =>
        {
            int argCount = ctx.Call.Arguments.Arguments.Count;

            // Must have at least 1 argument (the event name).
            if (argCount < 1)
            {
                ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooFewArguments, "luinotifyeventtospectators", argCount, 1);
                return ScrData.Void;
            }

            // If arg2 (parameter count) is provided and is a constant int, validate the remaining arg count.
            if (argCount >= 2)
            {
                ExprNode? paramCountArg = GetArgument(ctx.Call, 1);
                if (paramCountArg is DataExprNode { Type: ScrDataTypes.Int, Value: int declaredParamCount })
                {
                    int expectedTotal = 2 + declaredParamCount;
                    if (argCount < expectedTotal)
                    {
                        ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooFewArguments, "luinotifyeventtospectators", argCount, expectedTotal);
                    }
                    else if (argCount > expectedTotal)
                    {
                        ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooManyArguments, "luinotifyeventtospectators", argCount, expectedTotal);
                    }
                }
            }

            return ScrData.Void;
        }
    };

    public static readonly EmulatedFunction Assert = new()
    {
        Name = "assert",
        EmulateCall = static (in EmulationContext ctx) => ScrData.Void
    };

    // --- Type predicate functions ---
    // These all narrow the subject to the corresponding ScrDataTypes when true.

    public static readonly EmulatedFunction IsPlayer = CreateEntitySubTypePredicate(
        "isplayer", ScrEntityTypes.Player);
    public static readonly EmulatedFunction IsVehicle = CreateEntitySubTypePredicate(
        "isvehicle", ScrEntityTypes.Vehicle);
    public static readonly EmulatedFunction IsActor = CreateEntitySubTypePredicate(
        "isactor", ScrEntityTypes.Actor);
    public static readonly EmulatedFunction IsAi = CreateEntitySubTypesPredicate(
        "isai", ScrEntityTypes.Actor, ScrEntityTypes.Vehicle);
    public static readonly EmulatedFunction IsSentient = CreateEntitySubTypesPredicate(
        "issentient", ScrEntityTypes.Vehicle, ScrEntityTypes.Actor, ScrEntityTypes.Player);
    public static readonly EmulatedFunction IsVehicleSpawner = CreateTypePredicate("isvehiclespawner", ScrDataTypes.Entity);
    public static readonly EmulatedFunction IsSpawner = CreateTypePredicate("isspawner", ScrDataTypes.Entity);

    public static readonly EmulatedFunction IsArray = CreateTypePredicate("isarray", ScrDataTypes.Array);
    public static readonly EmulatedFunction IsString = CreateTypePredicate("isstring", ScrDataTypes.String);
    public static readonly EmulatedFunction IsInt = CreateTypePredicate("isint", ScrDataTypes.Int);
    public static readonly EmulatedFunction IsFloat = CreateTypePredicate("isfloat", ScrDataTypes.Float);
    public static readonly EmulatedFunction IsVec = CreateTypePredicate("isvec", ScrDataTypes.Vector);
    public static readonly EmulatedFunction IsStruct = CreateTypePredicate("isstruct", ScrDataTypes.Struct);
    public static readonly EmulatedFunction IsEntity = CreateTypePredicate("isentity", ScrDataTypes.Entity);
    public static readonly EmulatedFunction IsClass = CreateTypePredicate("isclass", ScrDataTypes.Object);
    public static readonly EmulatedFunction IsFunctionPtr = CreateTypePredicate("isfunctionptr", ScrDataTypes.FunctionPointer);

    /// <summary>
    /// Creates an emulated function for a simple type predicate (e.g., IsArray, IsString).
    /// These take 1 argument as a free call, or 0 arguments as a method call (CSC).
    /// When true, the subject is narrowed to the given type.
    /// </summary>
    private static EmulatedFunction CreateTypePredicate(string name, ScrDataTypes narrowToType)
    {
        return new EmulatedFunction
        {
            Name = name,
            EmulateCall = (in EmulationContext ctx) =>
            {
                ValidatePredicateArgCount(ctx, name);
                return new ScrData(ScrDataTypes.Bool);
            },
            EmulateNarrowing = (string? subjectName, in EmulationContext ctx) =>
            {
                if (subjectName is null)
                {
                    return null;
                }

                Dictionary<string, TypeNarrowing> whenTrue = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, TypeNarrowing> whenFalse = new(StringComparer.OrdinalIgnoreCase);

                // True: subject is the narrowed type
                whenTrue[subjectName] = new TypeNarrowing(narrowToType, 0);

                return new NarrowingResult(new ScrData(ScrDataTypes.Bool), whenTrue, whenFalse);
            }
        };
    }

    /// <summary>
    /// Creates an emulated function for an entity sub-type predicate that accepts multiple sub-types (e.g., IsSentient).
    /// When true, the subject is narrowed to Entity with any of the given sub-types.
    /// </summary>
    private static EmulatedFunction CreateEntitySubTypesPredicate(string name, params ScrEntityTypes[] entityTypes)
    {
        ImmutableHashSet<IScrDataSubType> subTypes = entityTypes
            .Select(t => (IScrDataSubType)new ScrDataEntityType { EntityType = t })
            .ToImmutableHashSet();

        return new EmulatedFunction
        {
            Name = name,
            EmulateCall = (in EmulationContext ctx) =>
            {
                ValidatePredicateArgCount(ctx, name);
                return new ScrData(ScrDataTypes.Bool);
            },
            EmulateNarrowing = (string? subjectName, in EmulationContext ctx) =>
            {
                if (subjectName is null)
                {
                    return null;
                }

                Dictionary<string, TypeNarrowing> whenTrue = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, TypeNarrowing> whenFalse = new(StringComparer.OrdinalIgnoreCase);

                whenTrue[subjectName] = new TypeNarrowing(
                    KeepMask: ScrDataTypes.Entity,
                    RemoveMask: 0,
                    RequireSubTypes: subTypes);

                return new NarrowingResult(new ScrData(ScrDataTypes.Bool), whenTrue, whenFalse);
            }
        };
    }

    /// <summary>
    /// Creates an emulated function for an entity sub-type predicate (e.g., IsPlayer).
    /// When true, the subject is narrowed to Entity with the given sub-type.
    /// </summary>
    private static EmulatedFunction CreateEntitySubTypePredicate(string name, ScrEntityTypes entityType)
    {
        return new EmulatedFunction
        {
            Name = name,
            EmulateCall = (in EmulationContext ctx) =>
            {
                ValidatePredicateArgCount(ctx, name);
                return new ScrData(ScrDataTypes.Bool);
            },
            EmulateNarrowing = (string? subjectName, in EmulationContext ctx) =>
            {
                if (subjectName is null)
                {
                    return null;
                }

                Dictionary<string, TypeNarrowing> whenTrue = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, TypeNarrowing> whenFalse = new(StringComparer.OrdinalIgnoreCase);

                whenTrue[subjectName] = new TypeNarrowing(
                    KeepMask: ScrDataTypes.Entity,
                    RemoveMask: 0,
                    RequireSubTypes: ImmutableHashSet.Create<IScrDataSubType>(
                        new ScrDataEntityType { EntityType = entityType }));

                return new NarrowingResult(new ScrData(ScrDataTypes.Bool), whenTrue, whenFalse);
            }
        };
    }

    /// <summary>
    /// Validates argument count for type predicates.
    /// GSC: free call with 1 arg. CSC: may be called on an entity with 0 args.
    /// </summary>
    private static void ValidatePredicateArgCount(in EmulationContext ctx, string name)
    {
        int argCount = ctx.Call.Arguments.Arguments.Count;
        int expectedArgs = ctx.Target is not null ? 0 : 1;

        if (argCount < expectedArgs)
        {
            ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooFewArguments, name, argCount, expectedArgs);
        }
        else if (argCount > expectedArgs)
        {
            ctx.AddDiagnostic(ctx.Call.Arguments.Range, GSCErrorCodes.TooManyArguments, name, argCount, expectedArgs);
        }
    }

    private static ExprNode? GetArgument(FunCallNode call, int index)
    {
        int i = 0;
        foreach (ExprNode? arg in call.Arguments.Arguments)
        {
            if (i == index)
            {
                return arg;
            }
            i++;
        }
        return null;
    }
}
