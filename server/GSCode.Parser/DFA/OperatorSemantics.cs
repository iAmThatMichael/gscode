using System.Diagnostics.CodeAnalysis;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;

namespace GSCode.Parser.DFA;

internal ref struct OperatorSemantics(ParserIntelliSense sense, AnalysisFlags flags)
{
    public ParserIntelliSense Sense { get; } = sense;
    public AnalysisFlags Flags { get; } = flags;

    private void AddDiagnostic(Range range, GSCErrorCodes code, params object[] args)
    {
        if (Flags.Silent)
        {
            return;
        }
        Sense.AddSpaDiagnostic(range, code, args);
    }

    /// <summary>
    /// Shared preamble for ==, !=, ===, !==. Returns true when the caller should return
    /// <paramref name="earlyResult"/> immediately; false when operands are valid and the
    /// caller should compute its own final value.
    /// </summary>
    private bool TryEarlyReturnEquality(BinaryExprNode node, ScrData left, ScrData right,
        string op, out ScrData earlyResult)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            earlyResult = ScrData.Default;
            return true;
        }

        if (left.BooleanValue is null || right.BooleanValue is null)
        {
            earlyResult = new ScrData(ScrDataTypes.Bool);
            return true;
        }

        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, op, left.TypeToString(), right.TypeToString());
            earlyResult = ScrData.Default;
            return true;
        }

        if (left.HasType(ScrDataTypes.Undefined) && !left.Indeterminate)
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            earlyResult = new ScrData(ScrDataTypes.Bool);
            return true;
        }

        if (right.HasType(ScrDataTypes.Undefined) && !right.Indeterminate)
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            earlyResult = new ScrData(ScrDataTypes.Bool);
            return true;
        }

        earlyResult = default;
        return false;
    }

    public ScrData ExecuteCompoundOp(TokenType op, BinaryExprNode node, ScrData left, ScrData right)
    {
        return op switch
        {
            TokenType.PlusAssign => AnalyseAddOp(node, left, right),
            TokenType.MinusAssign => AnalyseMinusOp(node, left, right),
            TokenType.MultiplyAssign => AnalyseMultiplyOp(node, left, right),
            TokenType.DivideAssign => AnalyseDivideOp(node, left, right),
            TokenType.ModuloAssign => AnalyseModuloOp(node, left, right),
            TokenType.BitAndAssign => AnalyseBitAndOp(node, left, right),
            TokenType.BitOrAssign => AnalyseBitOrOp(node, left, right),
            TokenType.BitXorAssign => AnalyseBitXorOp(node, left, right),
            TokenType.BitLeftShiftAssign => AnalyseBitLeftShiftOp(node, left, right),
            TokenType.BitRightShiftAssign => AnalyseBitRightShiftOp(node, left, right),
            _ => ScrData.Default,
        };
    }

    public ScrData AnalyseAddOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }
        ScrDataTypes addOpMask = ScrDataTypes.Number | ScrDataTypes.Vector | ScrDataTypes.String | ScrDataTypes.Hash;
        if((left.Type & addOpMask) == ScrDataTypes.Void || (right.Type & addOpMask) == ScrDataTypes.Void)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "+", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // If both are numeric, we can add them together.
        if (left.IsNumeric() && right.IsNumeric())
        {
            if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
            {
                return new ScrData(ScrDataTypes.Int);
            }

            return new ScrData(ScrDataTypes.Float);
        }

        // If both are vectors, we can add them together.
        if (left.Type == ScrDataTypes.Vector && right.Type == ScrDataTypes.Vector)
        {
            // TODO: add vec3d addition
            return new ScrData(ScrDataTypes.Vector);
        }

        ScrDataTypes vectorOrNumericMask = ScrDataTypes.Vector | ScrDataTypes.Number;
        // At least one is a string, so do string concatenation. Won't be both numbers, as we checked that earlier.
        if (left.Type == ScrDataTypes.String || right.Type == ScrDataTypes.String || (left.Type & vectorOrNumericMask) == vectorOrNumericMask || (right.Type & vectorOrNumericMask) == vectorOrNumericMask)
        {
            return new ScrData(ScrDataTypes.String);
        }

        // If one or both are hashes, we can add them together.
        if (left.Type == ScrDataTypes.Hash || right.Type == ScrDataTypes.Hash)
        {
            // But the other must be a string if they aren't both hashes.
            if (left.Type == ScrDataTypes.Hash && right.Type == ScrDataTypes.String)
            {
                return new ScrData(ScrDataTypes.Hash);
            }
            if (right.Type == ScrDataTypes.Hash && left.Type == ScrDataTypes.String)
            {
                return new ScrData(ScrDataTypes.Hash);
            }
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "+", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Some union of types, but we won't compute it here for the moment. TODO change
        return ScrData.Default;
    }

    public ScrData AnalyseMinusOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if(TryHandleNumericBinaryOperation(left, right, out ScrData? result))
        {
            return result!.Value;
        }

        // ERROR: Operator '-' cannot be applied on operands of type ...
        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "-", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseMultiplyOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if(TryHandleNumericBinaryOperation(left, right, out ScrData? result))
        {
            return result!.Value;
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "*", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseDivideOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }


        ScrDataTypes numericMask = ScrDataTypes.Number | ScrDataTypes.Vector;
        if((left.Type & numericMask) == ScrDataTypes.Void || (right.Type & numericMask) == ScrDataTypes.Void)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "/", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Both are numeric, so result is a float.
        if (left.IsNumeric() && right.IsNumeric())
        {
            // If the right isn't truthy, then this is an attempted divide by zero.
            if(right.BooleanValue == false)
            {
                AddDiagnostic(node.Range, GSCErrorCodes.DivisionByZero);
                return ScrData.Default;
            }
            return new ScrData(ScrDataTypes.Float);
        }

        // If left OR right is a vector, and the other is numeric, then they cast upward to vector.
        if ((left.Type == ScrDataTypes.Vector || right.Type == ScrDataTypes.Vector) && (left.IsNumeric() || right.IsNumeric()))
        {
            return new ScrData(ScrDataTypes.Vector);
        }

        // There's some union of types, but we won't compute it here for the moment. TODO change
        return ScrData.Default;
    }

    public ScrData AnalyseModuloOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Int) && right.IsCompatibleWith(ScrDataTypes.Int))
        {
            // If the right isn't truthy, then this is an attempted divide by zero.
            if (right.BooleanValue == false)
            {
                AddDiagnostic(node.Range, GSCErrorCodes.DivisionByZero);
                return ScrData.Default;
            }
            return new ScrData(ScrDataTypes.Int);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "%", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseBitLeftShiftOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Int) && right.IsCompatibleWith(ScrDataTypes.Int))
        {
            return new ScrData(ScrDataTypes.Int);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "<<", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseBitRightShiftOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Int) && right.IsCompatibleWith(ScrDataTypes.Int))
        {
            return new ScrData(ScrDataTypes.Int);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, ">>", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (TryEarlyReturnEquality(node, left, right, "==", out ScrData earlyResult))
            return earlyResult;
        // TODO: this is a blunt instrument and I don't think it's correct
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue == right.BooleanValue);
    }

    public ScrData AnalyseNotEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (TryEarlyReturnEquality(node, left, right, "!=", out ScrData earlyResult))
            return earlyResult;
        // TODO: this is a blunt instrument and I don't think it's correct
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue != right.BooleanValue);
    }

    public ScrData AnalyseIdentityEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (TryEarlyReturnEquality(node, left, right, "===", out ScrData earlyResult))
            return earlyResult;
        // TODO: this is definitely not right.
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue == right.BooleanValue && left.Type == right.Type);
    }

    public ScrData AnalyseIdentityNotEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (TryEarlyReturnEquality(node, left, right, "!==", out ScrData earlyResult))
            return earlyResult;
        // TODO: this is definitely not right.
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue != right.BooleanValue || left.Type != right.Type);
    }

    public ScrData AnalyseAndOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }

        if (left.BooleanValue is null || right.BooleanValue is null)
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        // Undefined can't be compared, that's what isdefined is for.
        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "&&", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Warn them if either side is possibly undefined — but not if the type is
        // indeterminate, since CFA already merged the paths and the comparison is valid.
        if (left.HasType(ScrDataTypes.Undefined) && !left.Indeterminate)
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }
        if (right.HasType(ScrDataTypes.Undefined) && !right.Indeterminate)
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }

        // TODO: evaluate more intelligently - ie check if both are booleanish.
        return new ScrData(ScrDataTypes.Bool, booleanValue: left.BooleanValue == right.BooleanValue);
    }

    public ScrData AnalyseOrOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.TypeUnknown() || right.TypeUnknown())
        {
            return ScrData.Default;
        }
        if (left.BooleanValue is null || right.BooleanValue is null)
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        // Undefined can't be compared, that's what isdefined is for.
        if (left.Type == ScrDataTypes.Undefined || right.Type == ScrDataTypes.Undefined)
        {
            AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "||", left.TypeToString(), right.TypeToString());
            return ScrData.Default;
        }

        // Warn them if either side is possibly undefined — but not if the type is
        // indeterminate, since CFA already merged the paths and the comparison is valid.
        if (left.HasType(ScrDataTypes.Undefined) && !left.Indeterminate)
        {
            AddDiagnostic(node.Left!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }
        if (right.HasType(ScrDataTypes.Undefined) && !right.Indeterminate)
        {
            AddDiagnostic(node.Right!.Range, GSCErrorCodes.PossibleUndefinedComparison);
            return new ScrData(ScrDataTypes.Bool);
        }

        // TODO: evaluate more intelligently - ie check if both are booleanish.
        return new ScrData(ScrDataTypes.Bool);
    }

    public ScrData AnalyseBitAndOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Int) && right.IsCompatibleWith(ScrDataTypes.Int))
        {
            return new ScrData(ScrDataTypes.Int);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "&", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseBitOrOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Int) && right.IsCompatibleWith(ScrDataTypes.Int))
        {
            return new ScrData(ScrDataTypes.Int);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "|", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseBitXorOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Int) && right.IsCompatibleWith(ScrDataTypes.Int))
        {
            return new ScrData(ScrDataTypes.Int);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "^", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseGreaterThanOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Number) && right.IsCompatibleWith(ScrDataTypes.Number))
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, ">", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseLessThanOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Number) && right.IsCompatibleWith(ScrDataTypes.Number))
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "<", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseGreaterThanEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Number) && right.IsCompatibleWith(ScrDataTypes.Number))
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, ">=", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public ScrData AnalyseLessThanEqualsOp(BinaryExprNode node, ScrData left, ScrData right)
    {
        if (left.IsCompatibleWith(ScrDataTypes.Number) && right.IsCompatibleWith(ScrDataTypes.Number))
        {
            return new ScrData(ScrDataTypes.Bool);
        }

        AddDiagnostic(node.Range, GSCErrorCodes.OperatorNotSupportedOnTypes, "<=", left.TypeToString(), right.TypeToString());
        return ScrData.Default;
    }

    public static bool TryHandleNumericBinaryOperation(ScrData left, ScrData right, [NotNullWhen(true)] out ScrData? result)
    {
        ScrDataTypes numericMask = ScrDataTypes.Number | ScrDataTypes.Vector;
        if((left.Type & numericMask) == ScrDataTypes.Void || (right.Type & numericMask) == ScrDataTypes.Void)
        {
            result = null;
            return false;
        }

        // Both are ints, so result is an int.
        if (left.Type == ScrDataTypes.Int && right.Type == ScrDataTypes.Int)
        {
            result = new ScrData(ScrDataTypes.Int);
            return true;
        }

        // At least one is a float, both numeric, so result is a float.
        if (left.IsNumeric() && right.IsNumeric())
        {
            result = new ScrData(ScrDataTypes.Float);
            return true;
        }

        // If left OR right is a vector, and the other is numeric, then they cast upward to vector.
        if ((left.Type == ScrDataTypes.Vector || right.Type == ScrDataTypes.Vector) && (left.IsNumeric() || right.IsNumeric()))
        {
            result = new ScrData(ScrDataTypes.Vector);
            return true;
        }

        // There's some union of types, but we won't compute it here for the moment. TODO change
        result = ScrData.Default;
        return true;
    }
}
