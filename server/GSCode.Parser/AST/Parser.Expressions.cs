using GSCode.Data;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.AST;

internal ref partial struct Parser
{
    /// <summary>
    /// Parses and outputs a full expression that allows assignment.
    /// </summary>
    /// <remarks>
    /// AssignmentExpr := Expr AssignOp
    /// </remarks>
    /// <returns></returns>
    private ExprNode? AssignableExpr()
    {
        // TODO: Fault tolerant logic
        // Parse the left-hand side of the assignment
        // TODO: in practice, this could return null.
        ExprNode? left = Expr();
        if (left is null)
        {
            return null;
        }

        // Parse the assignment operator
        return AssignOp(left);
    }

    /// <summary>
    /// Parses and outputs an assignment operator.
    /// </summary>
    /// <remarks>
    /// AssignOp := (ASSIGN | PLUSASSIGN | MULTIPLYASSIGN | MODULOASSIGN | MINUSASSIGN | DIVIDEASSIGN | BITORASSIGN |
    ///             BITXORASSIGN | BITANDASSIGN | BITLEFTSHIFTASSIGN | BITRIGHTSHIFTASSIGN) Expr | INCREMENT | DECREMENT
    /// </remarks>
    /// <returns></returns>
    private ExprNode AssignOp(ExprNode left)
    {
        switch (CurrentTokenType)
        {
            case TokenType.Increment:
            case TokenType.Decrement:
                return new PostfixExprNode(left, Consume());
            case TokenType.Assign:
            case TokenType.PlusAssign:
            case TokenType.MultiplyAssign:
            case TokenType.ModuloAssign:
            case TokenType.MinusAssign:
            case TokenType.DivideAssign:
            case TokenType.BitOrAssign:
            case TokenType.BitXorAssign:
            case TokenType.BitAndAssign:
            case TokenType.BitLeftShiftAssign:
            case TokenType.BitRightShiftAssign:
                Token operatorToken = Consume();
                ExprNode? right = Expr();
                return new BinaryExprNode(left, operatorToken, right);
            // No assignment - go with whatever LHS yielded
            default:
                return left;
        }
    }

    /// <summary>
    /// Parses and outputs a full arithmetic or logical expression.
    /// </summary>
    /// <remarks>
    /// Expr := LogAnd LogOrRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? Expr()
    {
        ExprNode? left = LogAnd();
        if (left is null)
        {
            return null;
        }

        return LogOrRhs(left);
    }

    /// <summary>
    /// Parses and outputs a logical OR expression, if present.
    /// </summary>
    /// <remarks>
    /// LogOrRhs := OR LogAnd LogOrRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode LogOrRhs(ExprNode left)
    {
        if (!ConsumeIfType(TokenType.Or, out Token? orToken))
        {
            return left;
        }

        // Parse the right-hand side of the OR expression
        ExprNode? right = LogAnd();

        // TODO: maybe we check for OR lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next OR expression
        return LogOrRhs(new BinaryExprNode(left, orToken, right));
    }

    /// <summary>
    /// Parses and outputs logical AND expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// LogAnd := BitOr LogAndRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? LogAnd()
    {
        ExprNode? left = BitOr();
        if (left is null)
        {
            return null;
        }

        return LogAndRhs(left);
    }

    /// <summary>
    /// Parses and outputs a logical AND expression, if present.
    /// </summary>
    /// <remarks>
    /// LogAndRhs := AND BitOr LogAndRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode LogAndRhs(ExprNode left)
    {
        if (!ConsumeIfType(TokenType.And, out Token? andToken))
        {
            return left;
        }

        // Parse the right-hand side of the AND expression
        ExprNode? right = BitOr();

        // TODO: as above, maybe we check for AND lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next AND expression
        return LogAndRhs(new BinaryExprNode(left, andToken, right));
    }

    /// <summary>
    /// Parses and outputs bitwise OR expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// BitOr := BitXor BitOrRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? BitOr()
    {
        ExprNode? left = BitXor();
        if (left is null)
        {
            return null;
        }

        return BitOrRhs(left);
    }

    /// <summary>
    /// Parses and outputs a bitwise OR expression, if present.
    /// </summary>
    /// <remarks>
    /// BitOrRhs := BITOR BitXor BitOrRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode BitOrRhs(ExprNode left)
    {
        if (!ConsumeIfType(TokenType.BitOr, out Token? bitOrToken))
        {
            return left;
        }

        // Parse the right-hand side of the BITOR expression
        ExprNode? right = BitXor();

        // TODO: as above, maybe we check for BITOR lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next BITOR expression
        return BitOrRhs(new BinaryExprNode(left, bitOrToken, right));
    }

    /// <summary>
    /// Parses and outputs bitwise XOR expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// BitXor := BitAnd BitXorRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? BitXor()
    {
        ExprNode? left = BitAnd();
        if (left is null)
        {
            return null;
        }

        return BitXorRhs(left);
    }

    /// <summary>
    /// Parses and outputs a bitwise XOR expression, if present.
    /// </summary>
    /// <remarks>
    /// BitXorRhs := BITXOR BitAnd BitXorRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode BitXorRhs(ExprNode left)
    {
        if (!ConsumeIfType(TokenType.BitXor, out Token? bitXorToken))
        {
            return left;
        }

        // Parse the right-hand side of the BITXOR expression
        ExprNode? right = BitAnd();

        // TODO: as above, maybe we check for BITXOR lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next BITXOR expression
        return BitXorRhs(new BinaryExprNode(left, bitXorToken, right));
    }

    /// <summary>
    /// Parses and outputs bitwise AND expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// BitAnd := EqOp BitAndRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? BitAnd()
    {
        ExprNode? left = EqOp();
        if (left is null)
        {
            return null;
        }

        return BitAndRhs(left);
    }

    /// <summary>
    /// Parses and outputs a bitwise AND expression, if present.
    /// </summary>
    /// <remarks>
    /// BitAndRhs := BITAND EqOp BitAndRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode BitAndRhs(ExprNode left)
    {
        if (!ConsumeIfType(TokenType.BitAnd, out Token? bitAndToken))
        {
            return left;
        }

        // Parse the right-hand side of the BITAND expression
        ExprNode? right = EqOp();

        // TODO: as above, maybe we check for BITAND lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next BITAND expression
        return BitAndRhs(new BinaryExprNode(left, bitAndToken, right));
    }

    /// <summary>
    /// Parses and outputs equality expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// EqOp := RelOp EqOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? EqOp()
    {
        ExprNode? left = RelOp();
        if (left is null)
        {
            return null;
        }

        return EqOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs an equality expression, if present.
    /// </summary>
    /// <remarks>
    /// EqOpRhs := (EQUALS | NOTEQUALS | IDENTITYEQUALS | IDENTITYNOTEQUALS) RelOp EqOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode EqOpRhs(ExprNode left)
    {
        if (CurrentTokenType != TokenType.Equals && CurrentTokenType != TokenType.NotEquals &&
            CurrentTokenType != TokenType.IdentityEquals && CurrentTokenType != TokenType.IdentityNotEquals)
        {
            return left;
        }

        Token operatorToken = Consume();

        // Parse the right-hand side of the equality expression
        ExprNode? right = RelOp();

        // TODO: as above, maybe we check for EqOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next equality expression
        return EqOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs relational expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// RelOp := BitShiftOp RelOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? RelOp()
    {
        ExprNode? left = BitShiftOp();
        if (left is null)
        {
            return null;
        }

        return RelOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs a relational expression, if present.
    /// </summary>
    /// <remarks>
    /// RelOpRhs := (LESSTHAN | LESSTHANEQUALS | GREATERTHAN | GREATERTHANEQUALS) BitShiftOp RelOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode RelOpRhs(ExprNode left)
    {
        if (CurrentTokenType != TokenType.LessThan && CurrentTokenType != TokenType.LessThanEquals &&
            CurrentTokenType != TokenType.GreaterThan && CurrentTokenType != TokenType.GreaterThanEquals)
        {
            return left;
        }

        Token operatorToken = Consume();

        // Parse the right-hand side of the relational expression
        ExprNode? right = BitShiftOp();

        // TODO: as above, maybe we check for RelOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next relational expression
        return RelOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs bit shift expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// BitShiftOp := AddiOp BitShiftOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? BitShiftOp()
    {
        ExprNode? left = AddiOp();
        if (left is null)
        {
            return null;
        }

        return BitShiftOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs a bit shift expression, if present.
    /// </summary>
    /// <remarks>
    /// BitShiftOpRhs := (BITLEFTSHIFT | BITRIGHTSHIFT) AddiOp BitShiftOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode BitShiftOpRhs(ExprNode left)
    {
        if (CurrentTokenType != TokenType.BitLeftShift && CurrentTokenType != TokenType.BitRightShift)
        {
            return left;
        }

        Token operatorToken = Consume();

        // Parse the right-hand side of the bit shift expression
        ExprNode? right = AddiOp();

        // TODO: as above, maybe we check for BitShiftOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next bit shift expression
        return BitShiftOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs additive expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// AddiOp := MulOp AddiOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? AddiOp()
    {
        ExprNode? left = MulOp();
        if (left is null)
        {
            return null;
        }

        return AddiOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs an additive expression, if present.
    /// </summary>
    /// <remarks>
    /// AddiOpRhs := (PLUS | MINUS) MulOp AddiOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode AddiOpRhs(ExprNode left)
    {
        if (CurrentTokenType != TokenType.Plus && CurrentTokenType != TokenType.Minus)
        {
            return left;
        }

        Token operatorToken = Consume();

        // Parse the right-hand side of the additive expression
        ExprNode? right = MulOp();

        // TODO: as above, maybe we check for AddiOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next additive expression
        return AddiOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs multiplicative expressions and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// MulOp := PrefixOp MulOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? MulOp()
    {
        ExprNode? left = PrefixOp();
        if (left is null)
        {
            return null;
        }

        return MulOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs a multiplicative expression, if present.
    /// </summary>
    /// <remarks>
    /// MulOpRhs := (MULTIPLY | DIVIDE | MODULO) PrefixOp MulOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode MulOpRhs(ExprNode left)
    {
        if (CurrentTokenType != TokenType.Multiply && CurrentTokenType != TokenType.Divide && CurrentTokenType != TokenType.Modulo)
        {
            return left;
        }

        Token operatorToken = Consume();

        // Parse the right-hand side of the multiplicative expression
        ExprNode? right = PrefixOp();

        // TODO: as above, maybe we check for MulOp lookahead, then try construct with unknown RHS/LHS
        if (right is null)
        {
            return left;
        }

        // Recurse to the next multiplicative expression
        return MulOpRhs(new BinaryExprNode(left, operatorToken, right));
    }

    /// <summary>
    /// Parses and outputs prefix operators and higher in precedence, if present.
    /// </summary>
    /// <remarks>
    /// PrefixOp := (PLUS | MINUS | BITNOT | NOT | BITAND) PrefixOp | CallOrAccessOp |
    ///             NEW Identifier LPAR RPAR
    /// </remarks>
    /// <returns></returns>
    private ExprNode? PrefixOp()
    {
        switch (CurrentTokenType)
        {
            case TokenType.Plus:
            case TokenType.Minus:
            case TokenType.BitNot:
            case TokenType.Not:
            case TokenType.BitAnd:
                Token operatorToken = Consume();

                // Parse the right-hand side of the prefix expression
                ExprNode? operand = PrefixOp();
                if (operand is null)
                {
                    return null;
                }

                return new PrefixExprNode(operatorToken, operand);
            case TokenType.New:
                Advance();

                // No need to do scope res, etc. as GSC strictly only looks for an identifier.

                if (!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
                {
                    AddError(GSCErrorCodes.ExpectedClassIdentifier, "identifier", CurrentToken.Lexeme);
                    return null;
                }

                // GSC doesn't let you pass arguments to constructors, which is hilarious

                // Check for LPAR - TODO: handle these more elegantly
                if (!AdvanceIfType(TokenType.OpenParen))
                {
                    AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
                }
                // Check for RPAR
                if (!AdvanceIfType(TokenType.CloseParen))
                {
                    AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
                }

                return new ConstructorExprNode(identifierToken);
            default:
                return CallOrAccessOp();
        }
    }

    /// <summary>
    /// Parses and outputs function call, accessor and higher precedence operations.
    /// </summary>
    /// <remarks>
    /// CallOrAccessOp := OPENBRACKET DerefOrArrayOp | Operand CallOrAccessOpRhs | THREAD DerefOrOperand CallOpRhs
    /// </remarks>
    /// <returns></returns>
    private ExprNode? CallOrAccessOp()
    {
        // Dereferenced operation or an array declaration
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            return DerefOrArrayOp(openBracket);
        }

        // Threaded call without called-on
        if (ConsumeIfType(TokenType.Thread, out Token? threadToken))
        {
            ExprNode? call = DerefOpOrOperandFunCall();
            if (call is null)
            {
                return null;
            }
            return new PrefixExprNode(threadToken, call);
        }

        // Could be a function call, operand, accessor, etc.
        ExprNode? left = Operand();
        if (left is null)
        {
            return null;
        }

        return CallOrAccessOpRhs(left);
    }

    /// <summary>
    /// Parses and outputs a dereference function call or a standard function call.
    /// </summary>
    /// <returns></returns>
    private ExprNode? DerefOpOrOperandFunCall()
    {
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            return DerefOp(openBracket);
        }

        ExprNode? left = Operand();
        if (left is null)
        {
            return null;
        }
        return CallOpRhs(left);
    }

    /// <summary>
    /// Parses a dereference-related operation or an array declaration.
    /// </summary>
    /// <remarks>
    /// DerefOrArrayOp := CLOSEBRACKET | DerefOp
    /// </remarks>
    /// <returns></returns>
    private ExprNode? DerefOrArrayOp(Token openBracket)
    {
        // Empty array
        if (ConsumeIfType(TokenType.CloseBracket, out Token? closeBracket))
        {
            return DataExprNode.EmptyArray(openBracket, closeBracket);
        }

        // Must be dereferencing
        return DerefOp(openBracket);
    }

    /// <summary>
    /// Parses a called-on dereference call operation or an array indexer.
    /// </summary>
    /// <remarks>
    /// CalledOnDerefOrIndexerOp := Expr CLOSEBRACKET CallOrAccessOpRhs | DerefOp
    /// </remarks>
    /// <param name="left"></param>
    /// <param name="openBracket"></param>
    /// <returns></returns>
    private ExprNode? CalledOnDerefOrIndexerOp(ExprNode left, Token openBracket)
    {
        if (CurrentTokenType == TokenType.OpenBracket)
        {
            ExprNode? derefCall = DerefOp(openBracket);

            if (derefCall is null)
            {
                return null;
            }

            return new CalledOnNode(left, derefCall);
        }

        ExprNode? index = Expr();

        // TODO: we should probably attempt a recovery mechanism if close bracket is encountered here, even if we can't provide info on what the array index is.

        // Check for CLOSEBRACKET
        if (!ConsumeIfType(TokenType.CloseBracket, out Token? closeBracket))
        {
            AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
        }

        return CallOrAccessOpRhs(new ArrayIndexNode(RangeHelper.From(left.Range.Start, closeBracket?.Range.End ?? index?.Range.End ?? left.Range.End), left, index));
    }

    /// <summary>
    /// Parses a dereference call operation.
    /// </summary>
    /// <remarks>
    /// DerefOp := OPENBRACKET Expr CLOSEBRACKET CLOSEBRACKET DerefCallOp
    /// </remarks>
    /// <param name="openBracket"></param>
    /// <returns></returns>
    private ExprNode? DerefOp(Token openBracket)
    {
        // TODO: fault tolerance
        if (!AdvanceIfType(TokenType.OpenBracket))
        {
            AddError(GSCErrorCodes.ExpectedToken, '[', CurrentToken.Lexeme);
        }

        // Parse the dereference expression
        ExprNode? derefExpr = Expr();

        if (derefExpr is null)
        {
            EnterRecovery();
        }

        // Check for CLOSEBRACKET, twice
        Position derefEnd = CurrentToken.Range.End;
        if (!AdvanceIfType(TokenType.CloseBracket))
        {
            AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
        }
        else
        {
            derefEnd = CurrentToken.Range.End;
            if (!AdvanceIfType(TokenType.CloseBracket))
            {
                AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
            }
        }

        // Wrap the expression in a DerefExprNode to indicate it came from [[ ]]
        DerefExprNode? wrappedDeref = derefExpr is not null
            ? new DerefExprNode(RangeHelper.From(openBracket.Range.Start, derefEnd), derefExpr)
            : null;

        ExprNode? result = DerefCallOp(openBracket, wrappedDeref);
        ExitRecovery();

        return result;
    }

    /// <summary>
    /// Parses the end of a dereference call operation.
    /// </summary>
    /// <remarks>
    /// DerefCallOp := ARROW IDENTIFIER FunCall | FunCall
    /// </remarks>
    /// <param name="openBracket"></param>
    /// <param name="derefExpr"></param>
    /// <returns></returns>
    private ExprNode? DerefCallOp(Token openBracket, ExprNode? derefExpr)
    {
        if (AdvanceIfType(TokenType.Arrow))
        {
            ExitRecovery();
            if (!ConsumeIfType(TokenType.Identifier, out Token? methodToken))
            {
                AddError(GSCErrorCodes.ExpectedMethodIdentifier, CurrentToken.Lexeme);
                return null;
            }

            ArgsListNode? methodArgs = FunCall();
            if (methodArgs is null)
            {
                return null;
            }

            MethodCallNode methodCall = new(openBracket.Range.Start, derefExpr, methodToken, methodArgs);
            return CallOrAccessOpRhs(methodCall);
        }

        ArgsListNode? funArgs = FunCall();
        if (funArgs is null)
        {
            return null;
        }

        FunCallNode call = new FunCallNode(openBracket.Range.Start, derefExpr, funArgs);
        return CallOrAccessOpRhs(call);
    }

    /// <summary>
    /// Parses the right-hand side of a function call, accessor operation, or called-on threaded/function calls.
    /// </summary>
    /// <remarks>
    /// CallOrAccessOpRhs := CallOpRhs | THREAD CalledOnCallOpRhs | CalledOnCallOpRhs | AccessOpRhs
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? CallOrAccessOpRhs(ExprNode left)
    {
        if (CurrentTokenType == TokenType.ScopeResolution || CurrentTokenType == TokenType.OpenParen)
        {
            return CallOpRhs(left);
        }

        // Threaded called-on ent call
        if (ConsumeIfType(TokenType.Thread, out Token? threadToken))
        {
            ExprNode? call = ThreadedCalledOnRhs(threadToken);
            if (call is null)
            {
                return null;
            }

            return new CalledOnNode(left, call);
        }

        // Called-on ent call
        if (CurrentTokenType == TokenType.Identifier || CurrentTokenType == TokenType.OpenBracket || CurrentTokenType == TokenType.Waittill || CurrentTokenType == TokenType.WaittillMatch)
        {
            return CalledOnRhs(left);
        }

        // Else, array index or accessor
        ExprNode? newLeft = AccessOpRhs(left);

        if (newLeft is null)
        {
            return null;
        }

        // Which itself could still be a called-on target
        if (CurrentTokenType == TokenType.Identifier || CurrentTokenType == TokenType.OpenBracket || CurrentTokenType == TokenType.Waittill || CurrentTokenType == TokenType.WaittillMatch)
        {
            return CalledOnRhs(newLeft);
        }
        return newLeft;
    }

    /// <summary>
    /// Parses the right-hand side of a called-on function call or an array indexer.
    /// </summary>
    /// <remarks>
    /// CalledOnRhs := WAITTILL WaittillRhs | WAITTILLMATCH WaittillMatchRhs | IDENTIFIER CallOpRhs | OPENBRACKET CalledOnDerefOrIndexerOp
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? CalledOnRhs(ExprNode left)
    {
        // Waittill operation
        if (ConsumeIfType(TokenType.Waittill, out Token? waitTillToken))
        {
            return WaittillRhs(left, waitTillToken);
        }

        // WaittillMatch operation
        if (ConsumeIfType(TokenType.WaittillMatch, out Token? waitTillMatchToken))
        {
            return WaittillMatchRhs(left, waitTillMatchToken);
        }

        // Called-on with identifier, so it's self foo::bar() or self foo()
        if (ConsumeIfType(TokenType.Identifier, out Token? functionQualifier))
        {
            IdentifierExprNode identifierExprNode = new(functionQualifier);
            if (CurrentTokenType != TokenType.ScopeResolution && CurrentTokenType != TokenType.OpenParen)
            {
                AddError(GSCErrorCodes.ExpectedFunctionQualification, CurrentToken.Lexeme);
                return null;
            }

            ExprNode? call = CallOpRhs(identifierExprNode);
            if (call is null)
            {
                return null;
            }
            return new CalledOnNode(left, call);
        }

        // Called on with dereference e.g. self [[ foo.bar ]](); OR an array indexer self[foo]
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            return CalledOnDerefOrIndexerOp(left, openBracket);
        }

        AddError(GSCErrorCodes.ExpectedFunctionIdentifier, CurrentToken.Lexeme);
        return null;
    }

    /// <summary>
    /// Parses the right-hand side of a wait till operation.
    /// </summary>
    /// <param name="left"></param>
    /// <param name="waitTillToken"></param>
    /// <remarks>
    /// WaittillRhs := OPENPAREN Expr WaittillVariables CLOSEPAREN
    /// </remarks>
    /// <returns></returns>
    private ExprNode? WaittillRhs(ExprNode left, Token waitTillToken)
    {
        // Check for OPENPAREN
        if (!ConsumeIfType(TokenType.OpenParen, out Token? openParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            return null;
        }

        // Parse the expression that gives us the wait till condition
        ExprNode? expr = Expr();
        if (expr is null)
        {
            return null;
        }

        // Then find any variable declarations that are populated when this wait till is hit.
        WaittillVariablesNode variables = WaittillVariables();

        if (!ConsumeIfType(TokenType.CloseParen, out Token? closeParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
            return new WaittillNode(left, expr, variables, RangeHelper.From(left.Range.Start, expr.Range.End));
        }

        return new WaittillNode(left, expr, variables, RangeHelper.From(left.Range.Start, closeParen.Range.End));
    }

    /// <summary>
    /// Parses zero-or-more wait till variable declarations.
    /// </summary>
    /// <remarks>
    /// WaittillVariables := COMMA (IDENTIFIER | UNDEFINED) WaittillVariables | ε
    /// </remarks>
    /// <returns></returns>
    private WaittillVariablesNode WaittillVariables()
    {
        // If no commas, then at the end of the list.
        if (!AdvanceIfType(TokenType.Comma))
        {
            return new();
        }

        // Check for undefined (ignored parameter placeholder)
        if (AdvanceIfType(TokenType.Undefined))
        {
            // Recurse to find any other waittill parameter names.
            WaittillVariablesNode rest = WaittillVariables();
            rest.Variables.AddFirst((IdentifierExprNode?)null);
            return rest;
        }

        // Seek to the next identifier
        if (!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
        {
            AddError(GSCErrorCodes.ExpectedWaittillIdentifier, CurrentToken.Lexeme);
            return new();
        }

        // Recurse to find any other waittill parameter names.
        WaittillVariablesNode others = WaittillVariables();
        others.Variables.AddFirst(new IdentifierExprNode(identifierToken));

        return others;
    }

    /// <summary>
    /// Parses the right-hand side of a waittillmatch operation.
    /// </summary>
    /// <param name="left"></param>
    /// <param name="waitTillMatchToken"></param>
    /// <remarks>
    /// WaittillMatchRhs := OPENPAREN Expr (COMMA Expr)? CLOSEPAREN
    /// </remarks>
    /// <returns></returns>
    private ExprNode? WaittillMatchRhs(ExprNode left, Token waitTillMatchToken)
    {
        // Check for OPENPAREN
        if (!ConsumeIfType(TokenType.OpenParen, out Token? openParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            return null;
        }

        // First required argument: notify name (string)
        ExprNode? notifyName = Expr();
        if (notifyName is null)
        {
            return null;
        }

        // Optional second argument: match value (string)
        ExprNode? matchValue = null;
        if (ConsumeIfType(TokenType.Comma, out _))
        {
            matchValue = Expr();
            if (matchValue is null)
            {
                return null;
            }
        }

        if (!ConsumeIfType(TokenType.CloseParen, out Token? closeParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
            return new WaittillMatchNode(left, notifyName, matchValue, RangeHelper.From(left.Range.Start, (matchValue ?? notifyName).Range.End));
        }

        return new WaittillMatchNode(left, notifyName, matchValue, RangeHelper.From(left.Range.Start, closeParen.Range.End));
    }

    /// <summary>
    /// Parses the right-hand side of a called-on threaded/function call operation.
    /// </summary>
    /// <remarks>
    /// ThreadedCalledOnRhs := IDENTIFIER CallOpRhs | OPENBRACKET DerefOp
    /// </remarks>
    /// <returns></returns>
    private ExprNode? ThreadedCalledOnRhs(Token threadToken)
    {
        // Neither matches
        if (CurrentTokenType != TokenType.Identifier && CurrentTokenType != TokenType.OpenBracket)
        {
            AddError(GSCErrorCodes.ExpectedFunctionIdentifier, CurrentToken.Lexeme);
            return null;
        }

        ExprNode? call = null;

        // Called-on with identifier, so it's self foo::bar() or self foo()
        if (ConsumeIfType(TokenType.Identifier, out Token? functionQualifier))
        {
            IdentifierExprNode identifierExprNode = new(functionQualifier);
            if (CurrentTokenType != TokenType.ScopeResolution && CurrentTokenType != TokenType.OpenParen)
            {
                AddError(GSCErrorCodes.ExpectedFunctionQualification, CurrentToken.Lexeme);
                return null;
            }

            call = CallOpRhs(identifierExprNode);
        }

        // Called on with dereference e.g. self [[ foo.bar ]]();
        else if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            call = DerefOp(openBracket);
        }

        if (call is null)
        {
            return null;
        }

        return new PrefixExprNode(threadToken, call);
    }

    /// <summary>
    /// Parses the right-hand side of a function call operation.
    /// </summary>
    /// <remarks>
    /// CallOpRhs := SCOPERESOLUTION Operand NamespacedFunctionRefOrCall CallOrAccessOpRhs | FunCall CallOrAccessOpRhs
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? CallOpRhs(ExprNode left)
    {
        if (AdvanceIfType(TokenType.ScopeResolution))
        {
            ExprNode? memberOfNamespace = Operand();
            if (memberOfNamespace is null)
            {
                return null;
            }

            NamespacedMemberNode namespacedMember = new(left, memberOfNamespace);

            // Could be a reference or a function call
            ExprNode? refOrCall = NamespacedFunctionRefOrCall(namespacedMember);
            if (refOrCall is null)
            {
                return null;
            }

            return CallOrAccessOpRhs(refOrCall);
        }

        // No namespace, just a function call
        if (CurrentTokenType == TokenType.OpenParen)
        {
            // TODO: fault tolerance
            ArgsListNode? functionArgs = FunCall();
            if (functionArgs is null)
            {
                return null;
            }
            FunCallNode call = new FunCallNode(left, functionArgs);

            // TODO: HACK - create permanent function hoverable solution at SPA-stage in a future version
            //if (left is IdentifierExprNode identifierExprNode && _scriptAnalyserData.GetApiFunction(identifierExprNode.Identifier) is ScrFunction function)
            //{
            //    Sense.AddSenseToken(identifierExprNode.Token, new DumbFunctionSymbol(identifierExprNode.Token, function));
            //}
            return CallOrAccessOpRhs(call);
        }

        AddError(GSCErrorCodes.ExpectedFunctionQualification, CurrentToken.Lexeme);
        return null;
    }

    /// <summary>
    /// Parses and outputs either a referenced to a namespaced function, or otherwise a call to that function.
    /// </summary>
    /// <remarks>
    /// NamespacedFunctionRefOrCall := FunCall | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? NamespacedFunctionRefOrCall(NamespacedMemberNode left)
    {
        // Not a call, just a reference.
        if (CurrentTokenType != TokenType.OpenParen)
        {
            return left;
        }

        // Call
        ArgsListNode? functionArgs = FunCall();
        if (functionArgs is null)
        {
            return null;
        }

        return new FunCallNode(left, functionArgs);
    }

    /// <summary>
    /// Parses and outputs a function call argument list node.
    /// </summary>
    /// <remarks>
    /// FunCall := OPENPAREN ArgsList CLOSEPAREN | OPENPAREN CLOSEPAREN
    /// </remarks>
    /// <returns></returns>
    private ArgsListNode? FunCall()
    {
        if (!ConsumeIfType(TokenType.OpenParen, out Token? openParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            return null;
        }

        // Empty argument list
        if (ConsumeIfType(TokenType.CloseParen, out Token? closeParen))
        {
            return new ArgsListNode
            {
                Range = RangeHelper.From(openParen.Range.Start, closeParen.Range.End)
            };
        }

        // Parse the argument list
        ArgsListNode argsList = ArgsList();

        // Check for CLOSEPAREN
        if (!ConsumeIfType(TokenType.CloseParen, out Token? lastToken))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
            lastToken = PreviousNode.Token;
        }

        argsList.Range = RangeHelper.From(openParen.Range.Start, lastToken.Range.End);
        return argsList;
    }

    /// <summary>
    /// Parses and outputs a 1-or-more argument list node.
    /// </summary>
    /// <remarks>
    /// ArgsList := Expr ArgsListRhs
    /// </remarks>
    /// <returns></returns>
    private ArgsListNode ArgsList()
    {
        ExprNode? firstArgument = Expr();

        ArgsListNode rest = ArgsListRhs();
        rest.Arguments.AddFirst(firstArgument);

        return rest;
    }

    /// <summary>
    /// Parses and outputs additional arguments in an argument list node.
    /// </summary>
    /// <remarks>
    /// ArgsListRhs := COMMA Expr ArgsListRhs | ε
    /// </remarks>
    /// <returns></returns>
    private ArgsListNode ArgsListRhs()
    {
        // No more arguments
        if (!AdvanceIfType(TokenType.Comma))
        {
            return new();
        }

        ExprNode? nextArgument = Expr();

        ArgsListNode rest = ArgsListRhs();
        rest.Arguments.AddFirst(nextArgument);

        return rest;
    }

    /// <summary>
    /// Parses and outputs the right-hand side of an accessor operation.
    /// </summary>
    /// <remarks>
    /// AccessOpRhs := DOT Operand CallOrAccessOpRhs | OPENBRACKET Expr CLOSEBRACKET CallOrAccessOpRhs | ε
    /// </remarks>
    /// <param name="left"></param>
    /// <returns></returns>
    private ExprNode? AccessOpRhs(ExprNode left)
    {
        // Accessor
        if (ConsumeIfType(TokenType.Dot, out Token? dotToken))
        {
            ExprNode? right = Operand();

            return CallOrAccessOpRhs(new BinaryExprNode(left, dotToken, right));
        }

        // Array index
        if (ConsumeIfType(TokenType.OpenBracket, out Token? openBracket))
        {
            ExprNode? index = Expr();

            // Check for CLOSEBRACKET
            if (!ConsumeIfType(TokenType.CloseBracket, out Token? closeBracket))
            {
                AddError(GSCErrorCodes.ExpectedToken, ']', CurrentToken.Lexeme);
            }

            return CallOrAccessOpRhs(new ArrayIndexNode(RangeHelper.From(left.Range.Start, closeBracket?.Range.End ?? index?.Range.End ?? openBracket.Range.End), left, index));
        }

        // Empty - just an operand further up
        return left;
    }

    /// <summary>
    /// Parses and outputs an operand within the context of an expression.
    /// </summary>
    /// <remarks>
    /// Operand :=  Number | String | Bool | OPENPAREN ParenExpr CLOSEPAREN | IDENTIFIER |
    ///             COMPILERHASH | ANIMIDENTIFIER | ANIMTREE
    /// </remarks>
    /// <returns></returns>
    private ExprNode? Operand()
    {
        switch (CurrentTokenType)
        {
            // All the primitives
            case TokenType.Integer:
            case TokenType.Float:
            case TokenType.Hex:
            case TokenType.String:
            case TokenType.IString:
            case TokenType.True:
            case TokenType.False:
            case TokenType.Undefined:
            case TokenType.CompilerHash:
            case TokenType.AnimTree:
                Token primitiveToken = CurrentToken;
                Advance();
                return DataExprNode.From(primitiveToken);
            // Could be a ternary expression, parenthesised expression, or a vector.
            case TokenType.OpenParen:
                Advance();
                ExprNode? parenExpr = ParenExpr();

                // Check for CLOSEPAREN
                if (!AdvanceIfType(TokenType.CloseParen))
                {
                    AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
                }

                return parenExpr;
            // Identifier & anim identifier
            case TokenType.Identifier:
            case TokenType.AnimIdentifier:
                Token identifierToken = CurrentToken;
                Advance();
                // TODO: doesn't actually use the anim identifier type
                return new IdentifierExprNode(identifierToken);
        }

        // Expected an operand
        AddError(GSCErrorCodes.ExpectedExpressionTerm, CurrentToken.Lexeme);
        return null;
    }

    /// <summary>
    /// Parses and outputs a parenthesised sub-expression, which could also be a ternary or vector expression.
    /// </summary>
    /// <remarks>
    /// ParenExpr := Expr ConditionalOrVector
    /// </remarks>
    /// <returns></returns>
    private ExprNode? ParenExpr()
    {
        ExprNode? expr = Expr();

        if (expr is null)
        {
            return null;
        }

        // Could be a ternary expression or a vector
        return ConditionalOrVector(expr);
    }

    /// <summary>
    /// Parses and outputs a ternary or vector expression if present.
    /// </summary>
    /// <param name="leftmostExpr">The leftmost subexpression of this node.</param>
    /// <remarks>
    /// ConditionalOrVector := QUESTIONMARK Expr COLON Expr | COMMA Expr COMMA Expr | ε
    /// </remarks>
    /// <returns></returns>
    private ExprNode? ConditionalOrVector(ExprNode leftmostExpr)
    {
        // Ternary expression
        if (AdvanceIfType(TokenType.QuestionMark))
        {
            ExprNode? trueExpr = Expr();

            if (trueExpr is null)
            {
                EnterRecovery();
            }

            // Check for COLON
            if (!AdvanceIfType(TokenType.Colon))
            {
                AddError(GSCErrorCodes.ExpectedToken, ':', CurrentToken.Lexeme);
                ExitRecovery();
                return null;
            }
            ExitRecovery();

            ExprNode? falseExpr = Expr();

            return new TernaryExprNode(leftmostExpr, trueExpr, falseExpr);
        }

        // Not a vector expression, just a parenthesised sub-expression
        if (!AdvanceIfType(TokenType.Comma))
        {
            return leftmostExpr;
        }

        // Vector expression

        ExprNode? secondExpr = Expr();

        if (secondExpr is null)
        {
            EnterRecovery();
        }

        // Check for COMMA
        if (!AdvanceIfType(TokenType.Comma))
        {
            AddError(GSCErrorCodes.ExpectedToken, ',', CurrentToken.Lexeme);
            EnterRecovery();
        }

        ExprNode? thirdExpr = Expr();

        ExitRecovery();
        return new VectorExprNode(leftmostExpr, secondExpr, thirdExpr);
    }
}
