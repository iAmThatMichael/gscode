using GSCode.Data;
using GSCode.Parser.Lexical;

namespace GSCode.Parser.AST;

internal ref partial struct Parser
{
    /// <summary>
    /// Parses and outputs a dependencies list.
    /// </summary>
    /// <remarks>
    /// Adaptation of: DependenciesList := Dependency DependenciesList | ε
    /// </remarks>
    /// <returns></returns>
    private List<DependencyNode> DependenciesList()
    {
        List<DependencyNode> dependencies = new List<DependencyNode>();

        while (CurrentTokenType == TokenType.Using)
        {
            DependencyNode? next = Dependency();

            // Success
            if (next is not null)
            {
                dependencies.Add(next);
                continue;
            }

            // Unsuccessful parse - attempt to recover
            EnterRecovery();

            // While we're not in the first set of ScriptList (or at EOF), keep advancing to try and recover.
            while (
                CurrentTokenType != TokenType.Precache &&
                CurrentTokenType != TokenType.UsingAnimTree &&
                CurrentTokenType != TokenType.Function &&
                CurrentTokenType != TokenType.Class &&
                CurrentTokenType != TokenType.Namespace &&
                CurrentTokenType != TokenType.Eof
                )
            {
                Advance();

                // We've recovered, so we can try to parse the next dependency.
                if (CurrentTokenType == TokenType.Using)
                {
                    ExitRecovery();
                    break;
                }
            }
        }

        return dependencies;
    }

    /// <summary>
    /// Parses and outputs a dependency node.
    /// </summary>
    /// <remarks>
    /// Dependency := USING Path SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private DependencyNode? Dependency()
    {
        // Pass USING
        Advance();

        // Parse the path
        PathNode? path = Path();
        if (path is null)
        {
            return null;
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "using directive");
        }

        return new DependencyNode(path);
    }

    /// <summary>
    /// Parses and outputs a path node.
    /// </summary>
    /// <remarks>
    /// Path := IDENTIFIER PathSub
    /// </remarks>
    /// <returns></returns>
    private PathNode? Path()
    {
        Token segmentToken = CurrentToken;
        if (CurrentTokenType != TokenType.Identifier)
        {
            // Expected a path segment
            AddError(GSCErrorCodes.ExpectedPathSegment, CurrentToken.Lexeme);
            return null;
        }

        // Path is whitespace-sensitive, so we'll advance manually.
        CurrentNode = CurrentNode.Next;

        PathNode? partial = PathPartial();

        if (partial is null)
        {
            return null;
        }

        partial.PrependSegment(segmentToken);
        return partial;
    }

    /// <summary>
    /// Parses and outputs a path partial node.
    /// </summary>
    /// <remarks>
    /// PathPartial := BACKSLASH IDENTIFIER PathPartial | ε
    /// </remarks>
    /// <returns></returns>
    private PathNode? PathPartial()
    {
        // Empty case
        if (CurrentTokenType != TokenType.Backslash)
        {
            return new PathNode();
        }

        // Path is whitespace-sensitive, so we'll advance manually.
        CurrentNode = CurrentNode.Next;

        Token segmentToken = CurrentToken;
        if (CurrentTokenType != TokenType.Identifier)
        {
            // Expected a path segment
            AddError(GSCErrorCodes.ExpectedPathSegment, CurrentToken.Lexeme);

            return null;
        }

        // Path is whitespace-sensitive, so we'll advance manually.
        CurrentNode = CurrentNode.Next;

        // Get any further segments, then we'll prepend the current one.
        PathNode? partial = PathPartial();

        // Failed to parse the rest of the path 
        if (partial is null)
        {
            return null;
        }
        partial.PrependSegment(segmentToken);

        return partial;
    }

    /// <summary>
    /// Parses and outputs a script definition list.
    /// </summary>
    /// <remarks>
    /// Adaptation of: ScriptList := ScriptDefn ScriptList | ε
    /// </remarks>
    /// <returns></returns>
    private List<AstNode> ScriptList()
    {
        List<AstNode> scriptDefns = new List<AstNode>();

        // Keep parsing script definitions until we reach the end of the file, as this is our last production.
        while (CurrentTokenType != TokenType.Eof &&
              (CurrentTokenType != TokenType.CloseDevBlock || !InDevBlock()))
        {
            AstNode? next = ScriptDefn();

            // Success
            if (next is not null)
            {
                scriptDefns.Add(next);

                // We're at the end of a dev block, so we can return the script definitions.
                if (CurrentTokenType == TokenType.CloseDevBlock && InDevBlock())
                {
                    return scriptDefns;
                }

                // We're not in recovery mode, so we can continue parsing as normal.
                if (!InRecovery())
                {
                    continue;
                }
            }

            // Unsuccessful parse - attempt to recover
            EnterRecovery();

            // While we're not in the first set of ScriptList (or at EOF), keep advancing to try and recover.
            while (
                CurrentTokenType != TokenType.Precache &&
                CurrentTokenType != TokenType.UsingAnimTree &&
                CurrentTokenType != TokenType.Function &&
                CurrentTokenType != TokenType.Class &&
                CurrentTokenType != TokenType.Namespace &&
                // Can't open a dev block - stop recovery when encountered
                CurrentTokenType != TokenType.OpenDevBlock &&
                // Can't be closing a dev block if we're not in one
                (CurrentTokenType != TokenType.CloseDevBlock || !InDevBlock()) &&
                CurrentTokenType != TokenType.Eof
                )
            {
                Advance();

                // We've recovered, so we can try to parse the next script definition.
                if (CurrentTokenType is TokenType.Precache or TokenType.UsingAnimTree or TokenType.Function or TokenType.Class or TokenType.Namespace ||
                   // Opening a dev block (including nested)
                   CurrentTokenType == TokenType.OpenDevBlock)
                {
                    ExitRecovery();
                    break;
                }
            }
        }

        return scriptDefns;
    }

    /// <summary>
    /// Parses and outputs a script definition node.
    /// </summary>
    /// <remarks>
    /// ScriptDefn := PrecacheDir | UsingAnimTreeDir | NamespaceDir | FunDefn | ClassDefn | DefnDevBlock
    /// </remarks>
    private AstNode? ScriptDefn()
    {
        switch (CurrentTokenType)
        {
            case TokenType.Precache:
                return PrecacheDir();
            case TokenType.UsingAnimTree:
                return UsingAnimTreeDir();
            case TokenType.Namespace:
                return NamespaceDir();
            case TokenType.Function:
                return FunDefn();
            case TokenType.Class:
                return ClassDefn();
            case TokenType.OpenDevBlock:
                return DefnDevBlock();
            case TokenType.Using:
                // The GSC compiler doesn't allow this, but we'll still attempt to parse it to get dependency info.
                AddError(GSCErrorCodes.UnexpectedUsing);
                return Dependency();
            // End of this dev block
            case TokenType.CloseDevBlock when InDevBlock():
                return null;
            case TokenType.Private:
            case TokenType.Autoexec:
                // They may be attempting to define a function with its modifiers in front, which is incorrect.
                AddError(GSCErrorCodes.UnexpectedFunctionModifier, CurrentToken.Lexeme);
                return null;
            default:
                // Expected a directive or definition
                AddError(GSCErrorCodes.ExpectedScriptDefn, CurrentToken.Lexeme);
                return null;
        }
    }

    /// <summary>
    /// Parses and outputs a script precache node.
    /// </summary>
    /// <remarks>
    /// PrecacheDir := PRECACHE OPENPAREN STRING COMMA STRING [COMMA] [STRING] CLOSEPAREN SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private PrecacheNode? PrecacheDir()
    {
        // Pass PRECACHE
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            return null;
        }

        // Parse the asset's type
        Token typeToken = CurrentToken;
        if (!AdvanceIfType(TokenType.String))
        {
            AddError(GSCErrorCodes.ExpectedPrecacheType, CurrentToken.Lexeme);
            return null;
        }

        // Check for COMMA
        if (!AdvanceIfType(TokenType.Comma))
        {
            AddError(GSCErrorCodes.ExpectedToken, ',', CurrentToken.Lexeme);
            return null;
        }

        // Parse the asset's path
        Token pathToken = CurrentToken;
        if (!AdvanceIfType(TokenType.String))
        {
            AddError(GSCErrorCodes.ExpectedPrecachePath, CurrentToken.Lexeme);
            return null;
        }

        // Optional COMMA
        if (AdvanceIfType(TokenType.Comma))
        {
            // Optional third STRING
            if (CurrentTokenType == TokenType.String)
            {
                // We don't care about the third string, so just advance past it.
                Advance();
            }
            else
            {
                AddError(GSCErrorCodes.ExpectedPrecachePath, CurrentToken.Lexeme);
            }
        }
        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);

            // We got enough information to create a node even if parsing failed.
            return new PrecacheNode()
            {
                Type = typeToken.Lexeme,
                TypeRange = typeToken.Range,
                Path = pathToken.Lexeme,
                PathRange = pathToken.Range
            };
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "precache directive");
        }

        // TODO: strip the quotes from the strings
        return new PrecacheNode()
        {
            Type = typeToken.Lexeme,
            TypeRange = typeToken.Range,
            Path = pathToken.Lexeme,
            PathRange = pathToken.Range
        };
    }

    /// <summary>
    /// Parses and outputs a using animtree node.
    /// </summary>
    /// <remarks>
    /// UsingAnimTreeDir := USINGANIMTREE OPENPAREN STRING CLOSEPAREN SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private UsingAnimTreeNode? UsingAnimTreeDir()
    {
        // Pass USINGANIMTREE
        Advance();

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            return null;
        }

        // Parse the animtree's name
        Token nameToken = CurrentToken;
        if (!AdvanceIfType(TokenType.String))
        {
            AddError(GSCErrorCodes.ExpectedAnimTreeName, CurrentToken.Lexeme);
            return null;
        }

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);

            // We got enough information to create a node even if parsing failed.
            return new UsingAnimTreeNode(nameToken);
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "using animation tree directive");
        }

        return new UsingAnimTreeNode(nameToken);
    }

    /// <summary>
    /// Parses and outputs a namespace node.
    /// </summary>
    /// <remarks>
    /// NamespaceDir := NAMESPACE IDENTIFIER SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private NamespaceNode? NamespaceDir()
    {
        // Pass NAMESPACE
        Advance();

        // Parse the namespace's identifier
        Token namespaceToken = CurrentToken;
        if (!AdvanceIfType(TokenType.Identifier))
        {
            AddError(GSCErrorCodes.ExpectedNamespaceIdentifier, CurrentToken.Lexeme);
            return null;
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "namespace directive");
        }

        return new NamespaceNode(namespaceToken);
    }

    /// <summary>
    /// Parses and outputs a dev block at script root level.
    /// </summary>
    /// <remarks>
    /// DefnDevBlock := OPENDEVBLOCK ScriptList CLOSEDEVBLOCK
    /// </remarks>
    /// <returns></returns>
    private AstNode DefnDevBlock()
    {
        bool isNewly = EnterContextIfNewly(ParserContextFlags.InDevBlock);

        // Pass OPENDEVBLOCK
        Advance();

        // The engine tracks the dev section as a flag, not a counter: a '/#' at script root
        // level while a dev block is already open is a redundant no-op, and the single '#/'
        // that follows closes the whole section (stock scripts rely on this, e.g.
        // vehicle_shared.gsc). Don't recurse and demand a matching '#/' of our own.
        if (!isNewly)
        {
            return new DefnDevBlockNode([]);
        }

        // Parse the script list within this dev block
        List<AstNode> scriptDefns = ScriptList();

        // Check for CLOSEDEVBLOCK
        if (!AdvanceIfType(TokenType.CloseDevBlock))
        {
            AddError(GSCErrorCodes.ExpectedToken, "#/", CurrentToken.Lexeme);
        }

        ExitContextIfWasNewly(ParserContextFlags.InDevBlock, isNewly);

        return new DefnDevBlockNode(scriptDefns);
    }

    /// <summary>
    /// Parses and outputs a class definition.
    /// </summary>
    /// <remarks>
    /// ClassDefn := CLASS IDENTIFIER InheritsFrom LBRACE ClassDefnList RBRACE
    /// </remarks>
    /// <returns></returns>
    private ClassDefnNode ClassDefn()
    {
        // Pass CLASS
        Advance();

        // Parse the class's identifier
        if (!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
        {
            AddError(GSCErrorCodes.ExpectedClassIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
        }

        // Check if we've got an inheritance clause
        Token? inheritedClassToken = InheritsFrom();

        // Check for LBRACE
        if (!ConsumeIfType(TokenType.OpenBrace, out Token? openBraceToken))
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);

            return new ClassDefnNode(identifierToken, inheritedClassToken, new ClassBodyListNode());
        }
        ExitRecovery();

        // Parse the class's body
        ClassBodyListNode classBody = ClassBodyDefnList();

        // Check for RBRACE
        if (!ConsumeIfType(TokenType.CloseBrace, out Token? closeBraceToken))
        {
            AddError(GSCErrorCodes.ExpectedToken, '}', CurrentToken.Lexeme);
            EnterRecovery();
        }

        EmitFoldingRangeIfPossible(openBraceToken, closeBraceToken);

        return new ClassDefnNode(identifierToken, inheritedClassToken, classBody);
    }

    /// <summary>
    /// Parses and outputs an inheritance clause, if present.
    /// </summary>
    /// <remarks>
    /// InheritsFrom := COLON IDENTIFIER | ε
    /// </remarks>
    /// <returns></returns>
    private Token? InheritsFrom()
    {
        if (!AdvanceIfType(TokenType.Colon))
        {
            return null;
        }

        ExitRecovery();

        if (!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
        {
            AddError(GSCErrorCodes.ExpectedClassIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
        }
        return identifierToken;
    }

    /// <summary>
    /// Parses and outputs zero or more class body definitions.
    /// </summary>
    /// <remarks>
    /// ClassDefnList := ClassDefn ClassDefnList | ε
    /// </remarks>
    /// <returns></returns>
    private ClassBodyListNode ClassBodyDefnList()
    {
        // Empty case
        if (CurrentTokenType != TokenType.Var && CurrentTokenType != TokenType.Constructor && CurrentTokenType != TokenType.Destructor && CurrentTokenType != TokenType.Function)
        {
            return new();
        }

        AstNode? classDefn = ClassBodyDefn();

        if (classDefn is null &&
            // No chance of recovery
            CurrentTokenType != TokenType.Var && CurrentTokenType != TokenType.Constructor && CurrentTokenType != TokenType.Destructor && CurrentTokenType != TokenType.Function)
        {
            return new();
        }

        ClassBodyListNode rest = ClassBodyDefnList();

        // Only add our first definition if it was successfully parsed
        if (classDefn is not null)
        {
            rest.Definitions.AddFirst(classDefn);
        }

        return rest;
    }

    /// <summary>
    /// Parses and outputs a class body definition.
    /// </summary>
    /// <remarks>
    /// ClassBodyDefn := MemberDecl | CONSTRUCTOR StructorDefn | DESTRUCTOR StructorDefn | FunDefn
    /// </remarks>
    /// <returns></returns>
    private AstNode? ClassBodyDefn()
    {
        switch (CurrentTokenType)
        {
            case TokenType.Var:
                return MemberDecl();
            case TokenType.Constructor:
            case TokenType.Destructor:
                Token keywordToken = Consume();

                return StructorDefn(keywordToken);
            case TokenType.Function:
                return FunDefn(isMethod: true);
            default:
                AddError(GSCErrorCodes.ExpectedClassBodyDefinition, CurrentToken.Lexeme);
                return null;
        }
    }

    /// <summary>
    /// Parses a class field declaration.
    /// </summary>
    /// <remarks>
    /// MemberDecl := VAR IDENTIFIER SEMICOLON
    /// </remarks>
    /// <returns></returns>
    private MemberDeclNode? MemberDecl()
    {
        // Pass VAR
        Advance();

        // Get the field name
        if (!ConsumeIfType(TokenType.Identifier, out Token? identifierToken))
        {
            AddError(GSCErrorCodes.ExpectedMemberIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
            return null;
        }

        // Check for SEMICOLON
        if (!AdvanceIfType(TokenType.Semicolon))
        {
            AddErrorAtEndOfPrevious(GSCErrorCodes.ExpectedSemiColon, "member declaration");
        }

        return new MemberDeclNode(identifierToken);
    }

    /// <summary>
    /// Parses a constructor or destructor definition.
    /// </summary>
    /// <remarks>
    /// StructorDefn := OPENPAREN CLOSEPAREN OPENBRACE FunBraceBlock CLOSEBRACE
    /// </remarks>
    /// <param name="keywordToken"></param>
    /// <returns></returns>
    private StructorDefnNode? StructorDefn(Token keywordToken)
    {
        // Keyword has already been consumed

        // Check for OPENPAREN
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
            EnterRecovery();
        }

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            // Were they trying to define a constructor with parameters?
            if (CurrentTokenType == TokenType.Identifier)
            {
                AddError(GSCErrorCodes.UnexpectedConstructorParameter, CurrentToken.Lexeme);
            }
            else
            {
                AddError(GSCErrorCodes.ExpectedConstructorParenthesis, CurrentToken.Lexeme);
            }
            EnterRecovery();
        }
        else
        {
            ExitRecovery();
        }

        // Check for OPENBRACE
        if (CurrentTokenType != TokenType.OpenBrace)
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);
            return null;
        }

        ExitRecovery();

        StmtListNode block = FunBraceBlock();

        // FunBraceBlock should have consumed the closing brace

        return new StructorDefnNode(keywordToken, block);
    }

    /// <summary>
    /// Parses and outputs a function definition node.
    /// </summary>
    /// <remarks>
    /// FunDefn := FUNCTION FunKeywords IDENTIFIER OPENPAREN ParamList CLOSEPAREN FunBraceBlock
    /// </remarks>
    /// <returns></returns>
    private FunDefnNode? FunDefn(bool isMethod = false)
    {
        // Pass FUNCTION
        Advance();

        FunKeywordsNode keywords = FunKeywords();

        // Parse the function's identifier
        Token? identifierToken = null;
        if (CurrentTokenType == TokenType.Identifier)
        {
            identifierToken = CurrentToken;
            Advance();
        }
        else
        {
            AddError(isMethod ? GSCErrorCodes.ExpectedMethodIdentifier : GSCErrorCodes.ExpectedFunctionIdentifier, CurrentToken.Lexeme);
            EnterRecovery();
        }

        // Check for OPENPAREN
        ParamListNode? parameters = null;
        if (!AdvanceIfType(TokenType.OpenParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, '(', CurrentToken.Lexeme);
        }
        else
        {
            ExitRecovery();

            // Parse the argument list.
            parameters = ParamList();
        }
        parameters ??= new ParamListNode();

        // Check for CLOSEPAREN
        if (!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
        }
        else
        {
            ExitRecovery();
        }

        // Check for the brace block, then parse it.
        if (CurrentTokenType != TokenType.OpenBrace)
        {
            AddError(GSCErrorCodes.ExpectedToken, '{', CurrentToken.Lexeme);
            ExitRecovery();

            // No use in doing a placeholder without a body, unless it's got a name.
            if (identifierToken is null)
            {
                return null;
            }

            // @next: Look at the FIRST set of Stmt and make a decision there on whether to enter the body despite the open brace not being there.
            return new FunDefnNode
            {
                Name = identifierToken,
                Keywords = keywords,
                Parameters = parameters,
                Body = new StmtListNode()
            };
        }

        ExitRecovery();
        StmtListNode block = FunBraceBlock();

        return new FunDefnNode
        {
            Name = identifierToken,
            Keywords = keywords,
            Parameters = parameters,
            Body = block
        };
    }

    /// <summary>
    /// Parses and outputs a brace block in a function.
    /// </summary>
    /// <remarks>
    /// FunBraceBlock := OPENBRACE StmtList CLOSEBRACE
    /// </remarks>
    /// <returns></returns>
    private StmtListNode FunBraceBlock()
    {
        // Pass over OPENBRACE
        Token openBraceToken = Consume();

        // Parse the statements in the block
        StmtListNode stmtListNode = StmtList();

        if (!ConsumeIfType(TokenType.CloseBrace, out Token? closeBraceToken))
        {
            AddError(GSCErrorCodes.ExpectedToken, '}', CurrentToken.Lexeme);
        }

        EmitFoldingRangeIfPossible(openBraceToken, closeBraceToken);

        return stmtListNode;
    }

    /// <summary>
    /// Parses a brace block, consuming a trailing '();' if present.
    /// This handles the edge case of <c>{...}();</c> (IIFE-like syntax).
    /// </summary>
    private StmtListNode BraceBlockWithOptionalCall()
    {
        StmtListNode block = FunBraceBlock();

        // Edge case: {stmts}(); — consume the trailing empty call and semicolon.
        if (CurrentTokenType == TokenType.OpenParen)
        {
            Advance(); // (
            if (!ConsumeIfType(TokenType.CloseParen, out _))
            {
                AddError(GSCErrorCodes.ExpectedToken, ')', CurrentToken.Lexeme);
            }
            if (!ConsumeIfType(TokenType.Semicolon, out _))
            {
                AddError(GSCErrorCodes.ExpectedToken, ';', CurrentToken.Lexeme);
            }
        }

        return block;
    }

    /// <summary>
    /// Parses and outputs a function keywords list node.
    /// </summary>
    /// <remarks>
    /// FunKeywords := PRIVATE FunKeywords | AUTOEXEC FunKeywords | ε
    /// </remarks>
    /// <returns></returns>
    private FunKeywordsNode FunKeywords()
    {
        // Empty production - base case
        if (CurrentTokenType != TokenType.Private && CurrentTokenType != TokenType.Autoexec)
        {
            return new();
        }

        // Got a keyword, prepend it to our keyword list
        Token keywordToken = CurrentToken;
        Advance();

        FunKeywordsNode node = FunKeywords();
        node.Keywords.AddFirst(keywordToken);

        return node;
    }

    /// <summary>
    /// Parses and outputs a function parameter list node.
    /// </summary>
    /// <remarks>
    /// ParamList := Param ParamListRhs | VARARGDOTS | ε
    /// </remarks>
    /// <returns></returns>
    private ParamListNode ParamList()
    {
        // varargdots production
        if (CurrentTokenType == TokenType.VarargDots)
        {
            Advance();
            return new([], true);
        }

        // empty production
        if (CurrentTokenType == TokenType.CloseParen)
        {
            return new();
        }

        // Try to parse a parameter
        ParamNode first = Param();

        // Seek the rest of them.
        ParamListNode rest = ParamListRhs();
        rest.Parameters.AddFirst(first);

        return rest;
    }

    /// <summary>
    /// Parses and outputs the right-hand side of a function parameter list.
    /// </summary>
    /// <remarks>
    /// ParamListRhs := COMMA Param | COMMA VARARGDOTS | ε
    /// </remarks>
    /// <returns></returns>
    private ParamListNode ParamListRhs()
    {
        // Nothing to add, base case.
        if (!AdvanceIfType(TokenType.Comma))
        {
            return new();
        }

        // varargdots production
        if (AdvanceIfType(TokenType.VarargDots))
        {
            return new([], true);
        }

        // Try to parse a parameter
        ParamNode next = Param();

        // Seek the rest of them.
        ParamListNode rest = ParamListRhs();
        rest.Parameters.AddFirst(next);

        return rest;
    }

    /// <summary>
    /// Parses and outputs a function parameter node.
    /// </summary>
    /// <remarks>
    /// Adaptation of: Param := BITAND IDENTIFIER ParamRhs | IDENTIFIER ParamRhs
    /// </remarks>
    /// <returns></returns>
    private ParamNode Param()
    {
        // Get whether we're passing by reference
        bool byRef = AdvanceIfType(TokenType.BitAnd);

        Token? nameToken = CurrentToken;

        // and get the parameter name.
        if (CurrentTokenType != TokenType.Identifier)
        {
            nameToken = null;
            AddError(GSCErrorCodes.ExpectedParameterIdentifier, CurrentToken.Lexeme);

            // Attempt error recovery
            if (CurrentTokenType is TokenType.Comma or TokenType.CloseParen)
            {
                return new(null, byRef);
            }
        }

        AdvanceIfType(TokenType.Identifier);

        ExprNode? defaultNode = ParamRhs();

        return new(nameToken, byRef, defaultNode);
    }

    /// <summary>
    /// Parses and outputs the default value of a function parameter, if given.
    /// </summary>
    /// <remarks>
    /// ParamRhs := ASSIGN Expr | ε
    /// </remarks>
    /// <returns></returns>
    private ExprNode? ParamRhs()
    {
        if (!AdvanceIfType(TokenType.Assign))
        {
            return null;
        }

        return Expr();
    }
}
