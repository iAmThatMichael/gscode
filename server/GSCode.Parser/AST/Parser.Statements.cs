using GSCode.Data;
using GSCode.Parser.Lexical;

namespace GSCode.Parser.AST;

internal ref partial struct Parser
{
    /// <summary>
    /// Parses a (possibly empty) list of statements in a function brace block.
    /// </summary>
    /// <remarks>
    /// StmtList := Stmt StmtList | ε
    /// </remarks>
    /// <returns></returns>
    private StmtListNode StmtList(ParserContextFlags newContext = ParserContextFlags.None)
    {
        bool isNewContext = false;
        if (newContext != ParserContextFlags.None)
        {
            isNewContext = EnterContextIfNewly(newContext);
        }

        switch (CurrentTokenType)
        {
            // Control flow
            case TokenType.If:
            case TokenType.Do:
            case TokenType.While:
            case TokenType.For:
            case TokenType.Foreach:
            case TokenType.Switch:
            case TokenType.Return:
            // Special functions
            case TokenType.WaittillFrameEnd:
            case TokenType.Wait:
            case TokenType.WaitRealTime:
            // Misc
            case TokenType.Const:
            case TokenType.OpenDevBlock:
            case TokenType.OpenBrace:
            case TokenType.Semicolon:
            // Contextual
            case TokenType.Break when InLoopOrSwitch():
            case TokenType.Continue when InLoop():
            // Expressions
            case TokenType.Identifier:
            case TokenType.OpenBracket:
            case TokenType.Thread:
                AstNode? statement = Stmt();

                StmtListNode rest = StmtList();
                if (statement is not null)
                {
                    rest.Statements.AddFirst(statement);
                }

                ExitContextIfWasNewly(newContext, isNewContext);
                return rest;
            // Everything else - empty case
            default:
                ExitContextIfWasNewly(newContext, isNewContext);
                return new();
        }
    }

    /// <summary>
    /// Parses a single statement in a function.
    /// </summary>
    /// <remarks>
    /// Stmt := IfElseStmt | DoWhileStmt | WhileStmt | ForStmt | ForeachStmt | SwitchStmt | ReturnStmt | WaittillFrameEndStmt | WaitStmt | WaitRealTimeStmt | ConstStmt | DevBlock | BraceBlock | ExprStmt
    /// </remarks>
    /// <returns></returns>
    private AstNode? Stmt(ParserContextFlags newContext = ParserContextFlags.None)
    {
        bool isNewContext = false;
        if (newContext != ParserContextFlags.None)
        {
            isNewContext = EnterContextIfNewly(newContext);
        }

        AstNode? result = CurrentTokenType switch
        {
            TokenType.If => IfElseStmt(),
            TokenType.Do => DoWhileStmt(),
            TokenType.While => WhileStmt(),
            TokenType.For => ForStmt(),
            TokenType.Foreach => ForeachStmt(),
            TokenType.Switch => SwitchStmt(),
            TokenType.Return => ReturnStmt(),
            TokenType.WaittillFrameEnd => ControlFlowActionStmt(AstNodeType.WaitTillFrameEndStmt),
            TokenType.Wait => ReservedFuncStmt(AstNodeType.WaitStmt),
            TokenType.WaitRealTime => ReservedFuncStmt(AstNodeType.WaitRealTimeStmt),
            TokenType.Const => ConstStmt(),
            TokenType.OpenDevBlock => FunDevBlock(),
            TokenType.OpenBrace => BraceBlockWithOptionalCall(),
            TokenType.Break when InLoopOrSwitch() => ControlFlowActionStmt(AstNodeType.BreakStmt),
            TokenType.Continue when InLoop() => ControlFlowActionStmt(AstNodeType.ContinueStmt),
            TokenType.Semicolon => EmptyStmt(),
            TokenType.Identifier or TokenType.Thread or TokenType.OpenBracket => ExprStmt(),
            _ => null
        };

        ExitContextIfWasNewly(newContext, isNewContext);
        return result;
    }

    /// <summary>
    /// Parses an if-else statement in a function block.
    /// </summary>
    /// <remarks>
    /// IfElseStmt := IfClause ElseOrEndClause
    /// </remarks>
    /// <returns></returns>
    private IfStmtNode IfElseStmt()
    {
        IfStmtNode firstClause = IfClause();

        // May go into another clause
        firstClause.Else = ElseOrEndClause();

        return firstClause;
    }

    /// <summary>
    /// Parses a single if-clause and its then statement.
    /// </summary>
    /// <remarks>
    /// IfClause := IF OPENPAREN Expr CLOSEPAREN Stmt
    /// </remarks>
    /// <returns></returns>
    private IfStmtNode IfClause()
    {
        // TODO: Fault tolerant logic
        // Pass IF
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }

        // Parse the condition
        ExprNode? condition = Expr();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }

        // Parse the then branch
        AstNode? then = Stmt();

        return new(condition)
        {
            Then = then
        };
    }

    /// <summary>
    /// Parses an else or else-if clause if provided, or otherwise ends the if statement.
    /// </summary>
    /// <remarks>
    /// ElseOrEndClause := ELSE ElseClause | ε
    /// </remarks>
    /// <returns></returns>
    private IfStmtNode? ElseOrEndClause()
    {
        // Empty production
        if (!AdvanceIfType(TokenType.Else))
        {
            return null;
        }

        // Otherwise, seek an else or else-if
        return ElseClause();
    }

    /// <summary>
    /// Parses an else or else-if clause, including its then statement and any further clauses.
    /// </summary>
    /// <remarks>
    /// ElseClause := IfClause ElseOrEndClause | Stmt
    /// </remarks>
    /// <returns></returns>
    private IfStmtNode ElseClause()
    {
        // Case 1: another if-clause
        if (CurrentTokenType == TokenType.If)
        {
            IfStmtNode clause = IfClause();

            // May go into another clause
            clause.Else = ElseOrEndClause();

            return clause;
        }

        // Case 2: just an else clause
        AstNode? then = Stmt();

        return new(null)
        {
            Then = then
        };
    }

    /// <summary>
    /// Parses a do-while statement, including its then clause and condition.
    /// </summary>
    /// <remarks>
    /// DoWhileStmt := DO Stmt WHILE OPENPAREN Expr CLOSEPAREN SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private DoWhileStmtNode DoWhileStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over DO
        Advance();

        // Parse the loop's body
        AstNode? then = Stmt(ParserContextFlags.InLoopBody);

        // Check for WHILE
        if (!AdvanceIfType(TokenType.While))
        {
            AddError(GSCErrorCodes.ExpectedToken, "while", CurrentToken.Lexeme);
            return new DoWhileStmtNode(null, then);
        }

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            EnterRecovery();
        }

        ExitRecovery();
        // Parse the loop's condition
        ExprNode? condition = Expr();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
            EnterRecovery();
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "do-while loop");
            EnterRecovery();
        }

        ExitRecovery();
        return new DoWhileStmtNode(condition, then);
    }

    /// <summary>
    /// Parses a while statement, including its condition and then clause.
    /// </summary>
    /// <remarks>
    /// WhileStmt := WHILE OPENPAREN Expr CLOSEPAREN Stmt
    /// </remarks>
    /// <returns></returns>
    private WhileStmtNode WhileStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over WHILE
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }

        // Parse the loop's condition
        ExprNode? condition = Expr();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }

        // Parse the loop's body, update context.
        AstNode? then = Stmt(ParserContextFlags.InLoopBody);

        return new WhileStmtNode(condition, then);
    }

    /// <summary>
    /// Parses a for statement, including its then clause and any initialization, condition, and increment clauses.
    /// </summary>
    /// <remarks>
    /// ForStmt := FOR OPENPAREN AssignableExpr SEMICOLON Expr SEMICOLON AssignableExpr CLOSEPAREN Stmt
    /// </remarks>
    /// <returns></returns>
    private ForStmtNode ForStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over FOR
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }

        // Parse the loop's initialization
        ExprNode? init = null;
        if (CurrentTokenType != TokenType.Semicolon)
        {
            init = AssignableExpr();
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "for loop");
        }

        // Parse the loop's condition
        ExprNode? condition = null;
        if (CurrentTokenType != TokenType.Semicolon)
        {
            condition = Expr();
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "for loop");
        }

        // Parse the loop's increment
        ExprNode? increment = null;
        if (CurrentTokenType != TokenType.CloseParen)
        {
            increment = AssignableExpr();
        }

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }

        // Parse the loop's body, update context.
        AstNode? then = Stmt(ParserContextFlags.InLoopBody);

        return new(init, condition, increment, then);
    }

    /// <summary>
    /// Parses a foreach statement, including its then clause and collection.
    /// </summary>
    /// <remarks>
    /// ForeachStmt := FOREACH OPENPAREN IDENTIFIER ForeachValueIdentifier IN Expr CLOSEPAREN Stmt
    /// </remarks>
    /// <returns></returns>
    private ForeachStmtNode ForeachStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over FOREACH
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }

        // Parse the loop's identifier
        Token firstIdentifierToken = CurrentToken;
        if (!AdvanceIfType(TokenType.Identifier))
        {
            AddError(GSCErrorCodes.ExpectedForeachIdentifier, CurrentToken.Lexeme);
        }

        // Check for COMMA and another identifier, which would indicate that we're iterating over key-value pairs.
        Token? valueIdentifierToken = ForeachValueIdentifier();

        // Check for IN
        if (!AdvanceIfType(TokenType.In))
        {
            AddError(GSCErrorCodes.ExpectedToken, "in", CurrentToken.Lexeme);
        }

        // Parse the loop's collection
        ExprNode? collection = Expr();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }

        // Parse the loop's body, update context.
        AstNode? then = Stmt(ParserContextFlags.InLoopBody);

        if (valueIdentifierToken is not null)
        {
            return new ForeachStmtNode(new IdentifierExprNode(valueIdentifierToken), new IdentifierExprNode(firstIdentifierToken), collection, then);
        }
        return new ForeachStmtNode(new IdentifierExprNode(firstIdentifierToken), null, collection, then);
    }

    /// <summary>
    /// Parses and outputs a foreach value token, if given. For key, value pairs.
    /// </summary>
    /// <remarks>
    /// ForeachValueIdentifier := COMMA IDENTIFIER | ε
    /// </remarks>
    /// <returns></returns>
    private Token? ForeachValueIdentifier()
    {
        if (!AdvanceIfType(TokenType.Comma))
        {
            return null;
        }

        if (!ConsumeIfType(TokenType.Identifier, out Token? valueToken))
        {
            AddError(GSCErrorCodes.ExpectedForeachIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
            return null;
        }
        return valueToken;
    }

    /// <summary>
    /// Parses and outputs a full switch statement.
    /// </summary>
    /// <remarks>
    /// SwitchStmt := SWITCH OPENPAREN Expr CLOSEPAREN OPENBRACE CaseList CLOSEBRACE
    /// </remarks>
    /// <returns></returns>
    private SwitchStmtNode SwitchStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over SWITCH
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }

        // Parse the switch's expression
        ExprNode? expression = Expr();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }

        // Check for OPENBRACE
        if (!ConsumeIfType(TokenType.OpenBrace, out Token? openBraceToken))
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);
        }

        // Parse the cases
        CaseListNode cases = CaseList();

        // Check for CLOSEBRACE
        if (!ConsumeIfType(TokenType.CloseBrace, out Token? closeBraceToken))
        {
            AddError(GSCErrorCodes.ExpectedToken, '}', CurrentToken.Lexeme);
        }

        EmitFoldingRangeIfPossible(openBraceToken, closeBraceToken);

        return new SwitchStmtNode()
        {
            Expression = expression,
            Cases = cases
        };
    }

    /// <summary>
    /// Parses and outputs a series of case labels and their associated statements.
    /// </summary>
    /// <remarks>
    /// CaseList := CaseStmt CaseList | ε
    /// </remarks>
    /// <returns></returns>
    private CaseListNode CaseList()
    {
        // Empty case
        if (CurrentTokenType != TokenType.Case && CurrentTokenType != TokenType.Default)
        {
            return new();
        }

        // Parse the current case, then prepend it to the rest of the cases.
        CaseStmtNode caseStmt = CaseStmt();
        CaseListNode rest = CaseList();

        rest.Cases.AddFirst(caseStmt);

        return rest;
    }

    /// <summary>
    /// Parses and outputs a case statement, which includes one or more case labels and their associated statements.
    /// </summary>
    /// <remarks>
    /// CaseStmt := CaseOrDefaultLabel CaseStmtRhs
    /// </remarks>
    /// <returns></returns>
    private CaseStmtNode CaseStmt()
    {
        CaseLabelNode label = CaseOrDefaultLabel();

        // Now go to the RHS
        CaseStmtNode rhs = CaseStmtRhs();
        rhs.Labels.AddFirst(label);

        return rhs;
    }

    /// <summary>
    /// Parses and outputs the right-hand result of a case statement, which includes more labels or the case's
    /// statement list.
    /// </summary>
    /// <remarks>
    /// CaseStmtRhs := CaseOrDefaultLabel CaseStmtRhs | StmtList
    /// </remarks>
    /// <returns></returns>
    private CaseStmtNode CaseStmtRhs()
    {
        if (CurrentTokenType == TokenType.Case || CurrentTokenType == TokenType.Default)
        {
            CaseLabelNode label = CaseOrDefaultLabel();

            // Self-recurse to exhaust all cases
            CaseStmtNode rhs = CaseStmtRhs();
            rhs.Labels.AddFirst(label);
            return rhs;
        }

        StmtListNode production = StmtList(ParserContextFlags.InSwitchBody);

        return new()
        {
            Body = production
        };
    }

    /// <summary>
    /// Parses and outputs a case label or default label.
    /// </summary>
    /// <remarks>
    /// CaseOrDefaultLabel := CASE Expr COLON | DEFAULT COLON
    /// </remarks>
    /// <returns></returns>
    private CaseLabelNode CaseOrDefaultLabel()
    {
        // Default label
        if (ConsumeIfType(TokenType.Default, out Token? defaultToken))
        {
            // Check for COLON
            if (!AdvanceIfType(TokenType.Colon))
            {
                AddError(GSCErrorCodes.ExpectedToken, ':', CurrentToken.Lexeme);
            }
            return new(AstNodeType.DefaultLabel, defaultToken);
        }

        // Case label
        if (!ConsumeIfType(TokenType.Case, out Token? caseToken))
        {
            AddError(GSCErrorCodes.ExpectedToken, "case", CurrentToken.Lexeme);
        }

        // Parse the case's expression
        ExprNode? expression = Expr();

        // Check for COLON
        if (!AdvanceIfType(TokenType.Colon))
        {
            AddError(GSCErrorCodes.ExpectedToken, ':', CurrentToken.Lexeme);
        }

        // In practice, this method is only ever hit if we've already detected a case label.
        return new(AstNodeType.CaseLabel, caseToken!, expression);
    }

    /// <summary>
    /// Parses a return statement.
    /// </summary>
    /// <remarks>
    /// Adaptation of: ReturnStmt := RETURN ReturnValue SEMICOLON
    /// where ReturnValue := Expr | ε
    /// </remarks>
    /// <returns></returns>
    private ReturnStmtNode ReturnStmt()
    {
        // Pass over RETURN
        Advance();

        // No return value
        if (AdvanceIfType(TokenType.Semicolon))
        {
            return new();
        }

        // Parse the return value
        ExprNode? value = Expr();

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "return statement");
        }

        return new(value);
    }

    /// <summary>
    /// Parses and outputs any control-flow specific action using the same method.
    /// </summary>
    /// <param name="type"></param>
    /// <remarks>
    /// WaittillFrameEndStmt := WAITTILLFRAMEEND SEMICOLON
    /// BreakStmt := BREAK SEMICOLON
    /// ContinueStmt := CONTINUE SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private ControlFlowActionNode ControlFlowActionStmt(AstNodeType type)
    {
        // Pass over the control flow keyword
        Token actionToken = CurrentToken;
        Advance();

        // Allow optional empty parentheses for waittillframeend (e.g. waittillframeend();)
        if (type == AstNodeType.WaitTillFrameEndStmt && AdvanceIfType(TokenType.OpenParen))
        {
            if (!AdvanceIfType(TokenType.CloseParen))
            {
                AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
            }
        }

        // Check for SEMICOLON
        if (AdvanceIfType(TokenType.Semicolon))
        {
            return new(type, actionToken);
        }

        string statementName = type switch
        {
            AstNodeType.WaitTillFrameEndStmt => "waittillframeend statement",
            AstNodeType.BreakStmt => "break statement",
            AstNodeType.ContinueStmt => "continue statement",
            _ => throw new ArgumentOutOfRangeException(nameof(type), "Invalid control flow action type")
        };
        AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, statementName);

        return new(type, actionToken);
    }

    /// <summary>
    /// Parses and outputs any reserved function using the same method.
    /// </summary>
    /// <param name="type"></param>
    /// <remarks>
    /// WaitStmt := WAIT Expr SEMICOLON
    /// WaitRealTimeStmt := WAITREALTIME Expr SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private ReservedFuncStmtNode ReservedFuncStmt(AstNodeType type)
    {
        // Pass over WAIT, WAITREALTIME, etc.
        Advance();

        // Get the function's expression
        ExprNode? expr = Expr();

        // Check for SEMICOLON
        if (AdvanceIfType(TokenType.Semicolon))
        {
            return new(type, expr);
        }

        string statementName = type switch
        {
            AstNodeType.WaitStmt => "wait statement",
            AstNodeType.WaitRealTimeStmt => "waitrealtime statement",
            _ => throw new ArgumentOutOfRangeException(nameof(type), "Invalid reserved function type")
        };
        AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, statementName);

        return new(type, expr);
    }

    /// <summary>
    /// Parses and outputs a constant declaration statement.
    /// </summary>
    /// <remarks>
    /// ConstStmt := CONST IDENTIFIER ASSIGN Expr SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private ConstStmtNode ConstStmt()
    {
        // TODO: Fault tolerant logic
        // Pass over CONST
        Advance();

        // Parse the constant's identifier
        Token identifierToken = CurrentToken;
        if (!AdvanceIfType(TokenType.Identifier))
        {
            AddError(GSCErrorCodes.ExpectedConstantIdentifier, CurrentToken.Lexeme);
        }

        // Check for ASSIGN
        if (!AdvanceIfType(TokenType.Assign))
        {
            AddError(GSCErrorCodes.ExpectedToken, '=', CurrentToken.Lexeme);
        }

        // Parse the constant's value
        ExprNode? value = Expr();

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "constant declaration");
        }

        return new ConstStmtNode(identifierToken, value);
    }

    /// <summary>
    /// Parses and outputs a dev block in the context of a function.
    /// </summary>
    /// <remarks>
    /// FunDevBlock := OPENDEVBLOCK StmtList CLOSEDEVBLOCK
    /// </remarks>
    /// <returns></returns>
    private FunDevBlockNode FunDevBlock()
    {
        bool isNewly = EnterContextIfNewly(ParserContextFlags.InDevBlock);

        // Pass over OPENDEVBLOCK
        Advance();

        // Parse the statements in the block
        StmtListNode stmtListNode = StmtList();

        // Check for CLOSEDEVBLOCK
        if (!AdvanceIfType(TokenType.CloseDevBlock))
        {
            AddError(GSCErrorCodes.ExpectedToken, "#/", CurrentToken.Lexeme);
        }

        ExitContextIfWasNewly(ParserContextFlags.InDevBlock, isNewly);
        return new FunDevBlockNode(stmtListNode);
    }

    /// <summary>
    /// Parses an expression statement.
    /// </summary>
    /// <remarks>
    /// ExprStmt := AssignableExpr SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private ExprStmtNode ExprStmt()
    {
        // Parse the expression
        ExprNode? expr = AssignableExpr();

        // The expression failed to parse - try to recover from this.
        if (expr is null)
        {
            // @next - make Stmt more fault-tolerant / better at recovery
            EnterRecovery();
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "expression statement");
        }

        return new ExprStmtNode(expr);
    }

    /// <summary>
    /// Parses an empty statement (ie just a semicolon.)
    /// </summary>
    /// <remarks>
    /// EmptyStmt := SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private EmptyStmtNode EmptyStmt()
    {
        // Pass over SEMICOLON
        Advance();

        return new();
    }
}
