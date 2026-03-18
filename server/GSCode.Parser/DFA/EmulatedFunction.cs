using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Components;

namespace GSCode.Parser.DFA;

/// <summary>
/// Per-symbol narrowing operation represented as bitmasks over <see cref="ScrDataTypes"/>,
/// with optional sub-type constraints for entity/object narrowing.
/// Intended meaning: newType = (oldType &amp; KeepMask) &amp; ~RemoveMask.
/// </summary>
internal readonly record struct TypeNarrowing(
    ScrDataTypes KeepMask,
    ScrDataTypes RemoveMask,
    ImmutableHashSet<IScrDataSubType>? RequireSubTypes = null);

/// <summary>
/// Describes the analysis behavior for a single emulated (built-in) function.
/// Each delegate field is optional — only non-null capabilities are invoked by the RDA.
/// </summary>
internal sealed class EmulatedFunction
{
    /// <summary>
    /// The canonical function name (matched case-insensitively).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Whether this function is called as a method (target Func()) rather than a free call (Func(target)).
    /// Controls how the narrowing subject is resolved.
    /// </summary>
    public bool IsMethodCall { get; init; }

    /// <summary>
    /// Custom argument validation and return type computation.
    /// Called during AnalyseFunctionCall. Receives the call context with pre-analysed argument types.
    /// Returns the ScrData return type.
    /// </summary>
    public EmulateCallDelegate? EmulateCall { get; init; }

    /// <summary>
    /// Type narrowing specification for when this function is used as a condition.
    /// If non-null, AnalyseCondition will produce narrowing facts.
    /// The subject parameter is the resolved variable name to narrow (from first arg or method target).
    /// </summary>
    public EmulateNarrowingDelegate? EmulateNarrowing { get; init; }
}

/// <summary>
/// Context passed to emulation delegates, providing access to the call site information.
/// </summary>
internal readonly struct EmulationContext
{
    /// <summary>The function call AST node.</summary>
    public required FunCallNode Call { get; init; }

    /// <summary>Pre-analysed argument types, in order.</summary>
    public required ScrData[] ArgumentTypes { get; init; }

    /// <summary>The method call target type, if this is a method call (target Func()). Null for free calls.</summary>
    public required ScrData? Target { get; init; }

    /// <summary>Whether we're in the silent (convergence) pass. When true, diagnostics should not be emitted.</summary>
    public required bool Silent { get; init; }

    /// <summary>The IntelliSense context for emitting diagnostics.</summary>
    public required ParserIntelliSense Sense { get; init; }

    /// <summary>
    /// Emits a diagnostic if not in silent mode.
    /// </summary>
    public void AddDiagnostic(Range range, GSCErrorCodes code, params object?[] args)
    {
        if (!Silent)
        {
            Sense.AddSpaDiagnostic(range, code, args);
        }
    }
}

/// <summary>
/// Delegate for custom call analysis. Returns the result type of the function call.
/// </summary>
internal delegate ScrData EmulateCallDelegate(in EmulationContext ctx);

/// <summary>
/// Result of narrowing emulation, mapping variable names to narrowing operations.
/// </summary>
internal readonly record struct NarrowingResult(
    ScrData Value,
    Dictionary<string, TypeNarrowing> WhenTrue,
    Dictionary<string, TypeNarrowing> WhenFalse);

/// <summary>
/// Delegate for producing type narrowing facts.
/// The subject parameter is the variable name to narrow (resolved by the caller from either
/// the first argument or the method call target).
/// Returns null if narrowing cannot be determined.
/// </summary>
internal delegate NarrowingResult? EmulateNarrowingDelegate(string? subjectName, in EmulationContext ctx);

/// <summary>
/// Static registry of emulated functions. Functions are matched case-insensitively by name.
/// </summary>
internal static class EmulatedFunctionRegistry
{
    private static readonly Dictionary<string, EmulatedFunction> s_functions =
        new(StringComparer.OrdinalIgnoreCase);

    static EmulatedFunctionRegistry()
    {
        Register(EmulatedFunctions.IsDefined);
        Register(EmulatedFunctions.VectorScale);
        Register(EmulatedFunctions.LuiNotifyEvent);
        Register(EmulatedFunctions.Assert);

        // Type predicates
        Register(EmulatedFunctions.IsPlayer);
        Register(EmulatedFunctions.IsVehicle);
        Register(EmulatedFunctions.IsActor);
        Register(EmulatedFunctions.IsAi);
        Register(EmulatedFunctions.IsSentient);
        Register(EmulatedFunctions.IsVehicleSpawner);
        Register(EmulatedFunctions.IsSpawner);
        Register(EmulatedFunctions.IsArray);
        Register(EmulatedFunctions.IsString);
        Register(EmulatedFunctions.IsInt);
        Register(EmulatedFunctions.IsFloat);
        Register(EmulatedFunctions.IsVec);
        Register(EmulatedFunctions.IsStruct);
        Register(EmulatedFunctions.IsEntity);
        Register(EmulatedFunctions.IsClass);
        Register(EmulatedFunctions.IsFunctionPtr);
    }

    private static void Register(EmulatedFunction function)
    {
        s_functions[function.Name] = function;
    }

    public static bool TryGet(string name, [NotNullWhen(true)] out EmulatedFunction? function)
    {
        return s_functions.TryGetValue(name, out function);
    }
}
