using GSCode.Data;
using GSCode.Parser.Lexical;

namespace GSCode.Parser.Pre;

internal ref partial struct Preprocessor
{
    /// <summary>
    /// Transforms a preprocessor if directive, emitting the tokens that meet the condition or any else tokens.
    /// </summary>
    private void IfDirective()
    {
        // The #if, expand to the right of it
        LinkedToken startAnchorToken = CurrentNode;
        Advance();

        // The condition
        int? condition = IfCondition();

        // For this branch to be taken, the condition must have parsed and resolved to a non-zero integer.
        bool conditionMet = condition is { } conditionInt && conditionInt != 0;

        // Go till we've definitely reached the end of the directive line
        while(CurrentTokenType != TokenType.LineBreak && CurrentTokenType != TokenType.Eof)
        {
            Advance();
        }

        LinkedToken endAnchorToken = CurrentNode;

        // We can now "delete" the if directive entirely.
        ConnectTokens(startAnchorToken.Previous!, endAnchorToken);

        // Track nesting level of #if directives
        int nestingLevel = 1;

        // Now get the whole branch.
        while(CurrentTokenType != TokenType.Eof)
        {
            // Entering nested if
            if(CurrentTokenType == TokenType.PreIf)
            {
                nestingLevel++;
            }
            // Exiting nested if
            else if(CurrentTokenType == TokenType.PreEndIf)
            {
                nestingLevel--;
                if(nestingLevel == 0)
                {
                    break;
                }
            }
            // If we're at the top level, we're done
            else if(nestingLevel == 1)
            {
                if(CurrentTokenType == TokenType.PreElIf || CurrentTokenType == TokenType.PreElse)
                {
                    break;
                }
            }
            Advance();
        }

        // If we're at the end of the file, we've got an error.
        if(CurrentTokenType == TokenType.Eof)
        {
            AddErrorAtLinkedToken(GSCErrorCodes.UnterminatedPreprocessorDirective, CurrentNode.Previous!, "#if");
            return;
        }

        // If the condition was not met, delete the whole branch.
        if(!conditionMet)
        {
            AddErrorAtRange(GSCErrorCodes.InactivePreprocessorBranch, RangeHelper.From(endAnchorToken.Next!.Range.Start, CurrentNode.Previous!.Range.End));
            ConnectTokens(startAnchorToken.Previous!, CurrentNode);
        }

        // If we're at #endif, we're done and need to delete it.
        if(CurrentTokenType == TokenType.PreEndIf)
        {
            ConnectTokens(startAnchorToken.Previous!, CurrentNode.Next!);
            Advance();
            return;
        }

        // Otherwise, we're at an #elif or #else, so we need to continue parsing.
        if(CurrentTokenType == TokenType.PreElse)
        {
            ElseDirective(conditionMet);
            return;
        }
        ElifDirective(conditionMet);

        // Finally, go back to the start of the directive so nested ones can be processed.
        CurrentNode = startAnchorToken.Previous!;
    }

    private void ElifDirective(bool conditionAlreadyMet)
    {
        // Store the elif token to delete it later.
        LinkedToken startAnchorToken = CurrentNode;
        Advance();

        // Parse the condition.
        int? conditionResult = IfCondition();
        bool conditionMet = !conditionAlreadyMet && conditionResult is int result && result != 0;

        // Go till we've definitely reached the end of the directive line
        while(CurrentTokenType != TokenType.LineBreak && CurrentTokenType != TokenType.Eof)
        {
            Advance();
        }

        LinkedToken endAnchorToken = CurrentNode;

        // We can now "delete" the elif directive entirely.
        ConnectTokens(startAnchorToken.Previous!, endAnchorToken);

        // Track nesting level of #if directives
        int nestingLevel = 1;

        // Now get the whole branch.
        while(CurrentTokenType != TokenType.Eof)
        {
            // Entering nested if
            if(CurrentTokenType == TokenType.PreIf)
            {
                nestingLevel++;
            }
            // Exiting nested if
            else if(CurrentTokenType == TokenType.PreEndIf)
            {
                nestingLevel--;
                if(nestingLevel == 0)
                {
                    break;
                }
            }
            // If we're at the top level, we're done
            else if(nestingLevel == 1)
            {
                if(CurrentTokenType == TokenType.PreElIf || CurrentTokenType == TokenType.PreElse)
                {
                    break;
                }
            }
            Advance();
        }

        // If we're at the end of the file, we've got an error.
        if(CurrentTokenType == TokenType.Eof)
        {
            AddErrorAtLinkedToken(GSCErrorCodes.UnterminatedPreprocessorDirective, CurrentNode.Previous!, "#elif");
            return;
        }

        // If the condition was not met, delete the whole branch.
        if(!conditionMet)
        {
            AddErrorAtRange(GSCErrorCodes.InactivePreprocessorBranch, RangeHelper.From(endAnchorToken.Next!.Range.Start, CurrentNode.Previous!.Range.End));
            ConnectTokens(startAnchorToken.Previous!, CurrentNode);
        }

        // If we're at #endif, we're done and need to delete it.
        if(CurrentTokenType == TokenType.PreEndIf)
        {
            ConnectTokens(startAnchorToken.Previous!, CurrentNode.Next!);
            Advance();
            return;
        }

        // Otherwise, we're at another #elif or #else, so we need to continue parsing.
        if(CurrentTokenType == TokenType.PreElse)
        {
            ElseDirective(conditionAlreadyMet || conditionMet);
            return;
        }
        ElifDirective(conditionAlreadyMet || conditionMet);
    }

    private void ElseDirective(bool conditionAlreadyMet)
    {
        // Store the else token to delete it later.
        LinkedToken startAnchorToken = CurrentNode;
        Advance();

        LinkedToken endAnchorToken = CurrentNode;

        // Track nesting level of #if directives
        int nestingLevel = 1;

        // Now get the whole branch.
        while(CurrentTokenType != TokenType.Eof)
        {
            // Entering nested if
            if(CurrentTokenType == TokenType.PreIf)
            {
                nestingLevel++;
            }
            // Exiting nested if
            else if(CurrentTokenType == TokenType.PreEndIf)
            {
                nestingLevel--;
                if(nestingLevel == 0)
                {
                    break;
                }
            }
            Advance();
        }

        // If we're at the end of the file, we've got an error.
        if(CurrentTokenType == TokenType.Eof)
        {
            AddErrorAtLinkedToken(GSCErrorCodes.UnterminatedPreprocessorDirective, CurrentNode.Previous!, "#else");
        }

        // Otherwise we're at the #endif

        // Delete the whole branch if a previous condition was met
        if(conditionAlreadyMet)
        {
            AddErrorAtRange(GSCErrorCodes.InactivePreprocessorBranch, RangeHelper.From(endAnchorToken.Next!.Range.Start, CurrentNode.Previous!.Range.End));
            ConnectTokens(startAnchorToken.Previous!, CurrentNode.Next!);
            Advance();
            return;
        }

        // Otherwise, delete the #else and the #endif but keep the rest.
        ConnectTokens(startAnchorToken.Previous!, startAnchorToken.Next!);

        if(CurrentTokenType == TokenType.PreEndIf)
        {
            ConnectTokens(CurrentNode.Previous!, CurrentNode.Next!);
            Advance();
        }
    }

    /// <summary>
    /// Parses an if directive's condition, starting with a logical OR operation.
    /// </summary>
    /// <returns></returns>
    private int? IfCondition()
    {
        int? leftResult = LogicalAndOp();
        if(leftResult is not int left)
        {
            return null;
        }

        return LogicalOrRhsOp(left);
    }

    /// <summary>
    /// Parses the right-hand side of an if directive's condition, starting with a logical OR operation.
    /// </summary>
    /// <param name="left"></param>
    /// <returns></returns>
    private int? LogicalOrRhsOp(int left)
    {
        if(!AdvanceIfType(TokenType.Or))
        {
            return left;
        }

        int? rightResult = LogicalAndOp();
        if(rightResult is not int right)
        {
            return null;
        }

        int localResult = (left != 0 || right != 0) ? 1 : 0;
        return LogicalOrRhsOp(localResult);
    }

    /// <summary>
    /// Parses the left-hand side of a logical AND operation in an if directive's condition.
    /// </summary>
    /// <returns></returns>
    private int? LogicalAndOp()
    {
        int? leftResult = EqualityOp();
        if(leftResult is not int left)
        {
            return null;
        }

        return LogicalAndRhsOp(left);
    }

    /// <summary>
    /// Parses the right-hand side of a logical AND operation in an if directive's condition.
    /// </summary>
    /// <param name="left"></param>
    /// <returns></returns>
    private int? LogicalAndRhsOp(int left)
    {
        if(!AdvanceIfType(TokenType.And))
        {
            return left;
        }

        int? rightResult = EqualityOp();
        if(rightResult is not int right)
        {
            return null;
        }

        int localResult = (left != 0 && right != 0) ? 1 : 0;
        return LogicalAndRhsOp(localResult);
    }

    /// <summary>
    /// Parses a logical equality operation in an if directive's condition.
    /// </summary>
    /// <returns></returns>
    private int? EqualityOp()
    {
        int? leftResult = RelationalOp();
        if(leftResult is not int left)
        {
            return null;
        }

        return EqualityRhsOp(left);
    }

    /// <summary>
    /// Parses the right-hand side of a logical equality operation in an if directive's condition.
    /// </summary>
    /// <param name="left"></param>
    /// <returns></returns>
    private int? EqualityRhsOp(int left)
    {
        TokenType operatorType = CurrentTokenType;
        if(operatorType != TokenType.Equals && operatorType != TokenType.NotEquals)
        {
            return left;
        }
        Advance();

        int? rightResult = RelationalOp();
        if(rightResult is not int right)
        {
            return null;
        }

        // Equality
        if(operatorType == TokenType.Equals)
        {
            return (left == right) ? 1 : 0;
        }
        // Inequality
        return (left != right) ? 1 : 0;
    }

    /// <summary>
    /// Parses a relational operation in an if directive's condition.
    /// </summary>
    /// <returns></returns>
    private int? RelationalOp()
    {
        int? targetResult = OpTarget();
        if(targetResult is not int target)
        {
            return null;
        }

        return RelationalRhsOp(target);
    }

    /// <summary>
    /// Parses the right-hand side of a relational operation in an if directive's condition.
    /// </summary>
    /// <param name="target"></param>
    /// <returns></returns>
    private int? RelationalRhsOp(int target)
    {
        TokenType operatorType = CurrentTokenType;
        if(operatorType != TokenType.LessThan && operatorType != TokenType.LessThanEquals && operatorType != TokenType.GreaterThan && operatorType != TokenType.GreaterThanEquals)
        {
            return target;
        }
        Advance();

        int? rightResult = OpTarget();
        if(rightResult is not int right)
        {
            return null;
        }

        return operatorType switch
        {
            TokenType.LessThan => (target < right) ? 1 : 0,
            TokenType.LessThanEquals => (target <= right) ? 1 : 0,
            TokenType.GreaterThan => (target > right) ? 1 : 0,
            TokenType.GreaterThanEquals => (target >= right) ? 1 : 0,
            _ => null
        };
    }

    /// <summary>
    /// Parses a target of an operation in an if directive's condition.
    /// </summary>
    /// <returns></returns>
    private int? OpTarget()
    {
        // Parenthesised sub-expression
        if(AdvanceIfType(TokenType.OpenParen))
        {
            int? result = IfCondition();
            if(result is not int resultInt)
            {
                return null;
            }

            if(!AdvanceIfType(TokenType.CloseParen))
            {
                return null;
            }
            return resultInt;
        }

        // Got an integer we can work with
        if(ConsumeIfType(TokenType.Integer, out LinkedToken? integerNode))
        {
            return int.Parse(integerNode.Lexeme);
        }

        // Token isn't supported - whole expression fails.
        return null;
    }
}
