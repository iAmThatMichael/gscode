using GSCode.Data;
using GSCode.Data.Models;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;
using Serilog;

namespace GSCode.Parser.DFA;

internal class SwitchAnalysisContext
{
    public ScrData SwitchExpressionType { get; set; } = ScrData.Default;
    public HashSet<string> SeenLabelValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<SwitchCaseDecisionNode> AnalyzedNodes { get; } = new();
    public bool HasDefault { get; set; } = false;
}

internal ref partial struct TypeFlowAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, List<Tuple<ScrClass, List<ControlFlowGraph>>> classGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable, ScriptAnalyserData? apiData = null, string? currentNamespace = null, HashSet<string>? knownNamespaces = null, string? fileName = null)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public List<Tuple<ScrClass, List<ControlFlowGraph>>> ClassGraphs { get; } = classGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;
    public ScriptAnalyserData? ApiData { get; } = apiData;
    public string? CurrentNamespace { get; } = currentNamespace;
    public HashSet<string>? KnownNamespaces { get; } = knownNamespaces;
    public string? FileName { get; } = fileName;

    public Dictionary<CfgNode, Dictionary<string, ScrVariable>> InSets { get; } = new();
    public Dictionary<CfgNode, Dictionary<string, ScrVariable>> OutSets { get; } = new();
    public Dictionary<(CfgNode From, CfgNode To), Dictionary<string, ScrVariable>> OutEdgeSets { get; } = new();

    public Dictionary<SwitchNode, SwitchAnalysisContext> SwitchContexts { get; } = new();

    public AnalysisFlags Flags { get; } = new();

    public bool Silent
    {
        get => Flags.Silent;
        set => Flags.Silent = value;
    }

    public OperatorSemantics Operators => new(Sense, Flags);

    public void Run()
    {
#if FLAG_PERFORMANCE_TRACKING
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int functionCount = 0;
        int classCount = 0;
        long timeInFunctions = 0;
        long timeInClasses = 0;
#endif

        foreach (Tuple<ScrFunction, ControlFlowGraph> functionGraph in FunctionGraphs)
        {
#if FLAG_PERFORMANCE_TRACKING
            var funcStart = sw.ElapsedMilliseconds;
#endif
            AnalyseFunction(functionGraph.Item1, functionGraph.Item2);
#if FLAG_PERFORMANCE_TRACKING
            functionCount++;
            timeInFunctions += sw.ElapsedMilliseconds - funcStart;
#endif
        }

        foreach (Tuple<ScrClass, List<ControlFlowGraph>> classEntry in ClassGraphs)
        {
#if FLAG_PERFORMANCE_TRACKING
            var classStart = sw.ElapsedMilliseconds;
#endif
            // Analyse each method independently with its class context
            foreach (ControlFlowGraph methodGraph in classEntry.Item2)
            {
                try
                {
                    AnalyseFunction(null!, methodGraph, currentClass: classEntry.Item1);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to analyse method in class {ClassName}", classEntry.Item1.Name);
                }
            }
#if FLAG_PERFORMANCE_TRACKING
            classCount++;
            timeInClasses += sw.ElapsedMilliseconds - classStart;
#endif
        }

#if FLAG_PERFORMANCE_TRACKING
        sw.Stop();
        Log.Debug("[PERF DETAIL RDA] Functions: {FuncCount} ({FuncTime}ms), Classes: {ClassCount} ({ClassTime}ms), Total: {Total}ms - File={File}",
            functionCount, timeInFunctions, classCount, timeInClasses, sw.ElapsedMilliseconds, FileName ?? "unknown");
#endif
    }

    public void AnalyseFunction(ScrFunction? function, ControlFlowGraph functionGraph, ScrClass? currentClass = null)
    {
        Silent = true;
        Sense.SilentSenseTokens = true;

        // Clear state at the start of each function analysis
        SwitchContexts.Clear();
        InSets.Clear();
        OutSets.Clear();
        OutEdgeSets.Clear();

#if FLAG_PERFORMANCE_TRACKING
        // Track time spent in different node types
        long timeInBasicBlocks = 0;
        long timeInDecisions = 0;
        long timeInIterations = 0;
        long timeInSwitches = 0;
        long timeInMergeSets = 0;
        long timeInUpdateEdges = 0;
        int basicBlockCount = 0;
        int decisionCount = 0;
        int iterationCount = 0;
        int switchCount = 0;
        var perfSw = System.Diagnostics.Stopwatch.StartNew();
#endif

        Stack<CfgNode> worklist = new();
        worklist.Push(functionGraph.Start);

        HashSet<CfgNode> visited = new();

        // Calculate iteration limit based on graph size to prevent infinite loops
        int totalNodes = CountAllNodes(functionGraph);
        int maxIterations = Math.Max(100, totalNodes * 10); // At least 100, or 10x nodes
        int iterations = 0;

        while (worklist.Count > 0 && iterations < maxIterations)
        {
            iterations++;
            CfgNode node = worklist.Pop();
            visited.Add(node);

#if FLAG_PERFORMANCE_TRACKING
            long nodeStart = perfSw.ElapsedTicks;
#endif

            // Calculate the in set
            Dictionary<string, ScrVariable> inSet = new(StringComparer.OrdinalIgnoreCase);
            foreach (CfgNode incoming in node.Incoming)
            {
                // Prefer edge-specific OUT (needed for type narrowing), fall back to node OUT.
                if (OutEdgeSets.TryGetValue((incoming, node), out Dictionary<string, ScrVariable>? edgeOut))
                {
                    inSet.MergeTables(edgeOut, node.Scope);
                    continue;
                }
                if (OutSets.TryGetValue(incoming, out Dictionary<string, ScrVariable>? nodeOut))
                {
                    inSet.MergeTables(nodeOut, node.Scope);
                }
            }

#if FLAG_PERFORMANCE_TRACKING
            timeInMergeSets += perfSw.ElapsedTicks - nodeStart;
            long processStart = perfSw.ElapsedTicks;
#endif

            // Check if the in set has changed, if not, then we can skip this node.
            if (InSets.TryGetValue(node, out Dictionary<string, ScrVariable>? currentInSet) && currentInSet.VariableTableEquals(inSet))
            {
                continue;
            }

            // Update the in & out sets
            InSets[node] = inSet;

            // Snapshot hash of previous outset for convergence check (avoids full dictionary copy)
            int? previousOutSetHash = null;
            if (OutSets.TryGetValue(node, out Dictionary<string, ScrVariable>? existingOutSet))
            {
                previousOutSetHash = existingOutSet.ComputeTableHash();
            }

            if (!OutSets.ContainsKey(node))
            {
                OutSets[node] = new Dictionary<string, ScrVariable>(StringComparer.OrdinalIgnoreCase);
            }

            ConditionResult? decisionCondition = null;
            ConditionResult? iterationCondition = null;

            // Calculate the out set
            if (node.Type == CfgNodeType.FunctionEntry)
            {
                AnalyseFunctionEntry((FunEntryBlock)node, inSet);
                OutSets[node] = inSet;
            }
            else if (node.Type == CfgNodeType.BasicBlock)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseBasicBlock((BasicBlock)node, symbolTable);

                OutSets[node] = symbolTable.VariableSymbols;
#if FLAG_PERFORMANCE_TRACKING
                basicBlockCount++;
                timeInBasicBlocks += perfSw.ElapsedTicks - processStart;
                processStart = perfSw.ElapsedTicks;
#endif
            }
            else if (node.Type == CfgNodeType.EnumerationNode)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseEnumeration((EnumerationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else if (node.Type == CfgNodeType.IterationNode)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                iterationCondition = AnalyseIterationInternal((IterationNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
#if FLAG_PERFORMANCE_TRACKING
                iterationCount++;
                timeInIterations += perfSw.ElapsedTicks - processStart;
                processStart = perfSw.ElapsedTicks;
#endif
            }
            else if (node.Type == CfgNodeType.DecisionNode)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                decisionCondition = AnalyseDecisionConditionInternal((DecisionNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
#if FLAG_PERFORMANCE_TRACKING
                decisionCount++;
                timeInDecisions += perfSw.ElapsedTicks - processStart;
                processStart = perfSw.ElapsedTicks;
#endif
            }
            else if (node.Type == CfgNodeType.SwitchNode)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseSwitch((SwitchNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
#if FLAG_PERFORMANCE_TRACKING
                switchCount++;
                timeInSwitches += perfSw.ElapsedTicks - processStart;
                processStart = perfSw.ElapsedTicks;
#endif
            }
            else if (node.Type == CfgNodeType.SwitchCaseDecisionNode)
            {
                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass);

                AnalyseSwitchCaseDecision((SwitchCaseDecisionNode)node, symbolTable);
                OutSets[node] = symbolTable.VariableSymbols;
            }
            else
            {
                OutSets[node] = inSet;
            }

#if FLAG_PERFORMANCE_TRACKING
            long edgeStart = perfSw.ElapsedTicks;
#endif

            // Update edge-specific out sets (used for branch-sensitive narrowing).
            bool edgeOutChanged = UpdateOutEdgeSetsForNode(node, OutSets[node],
                node.Type == CfgNodeType.DecisionNode ? decisionCondition : node.Type == CfgNodeType.IterationNode ? iterationCondition : null);

#if FLAG_PERFORMANCE_TRACKING
            timeInUpdateEdges += perfSw.ElapsedTicks - edgeStart;
#endif

            // Check if the outset has changed before queueing successors.
            bool outSetChanged = previousOutSetHash == null ||
                                 previousOutSetHash.Value != OutSets[node].ComputeTableHash() ||
                                 edgeOutChanged;

            // Only add successors to the worklist if the outset has changed
            if (!outSetChanged)
            {
                continue;
            }

            foreach (CfgNode successor in node.Outgoing)
            {
                worklist.Push(successor);
            }
        }

#if FLAG_PERFORMANCE_TRACKING
        perfSw.Stop();
        double msPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (basicBlockCount + decisionCount + iterationCount + switchCount > 10) // Only log for non-trivial functions
        {
            Log.Debug("[PERF DETAIL RDA Func] {FuncName}: BasicBlocks={BB}({BBms:F1}ms), Decisions={Dec}({Decms:F1}ms), Iterations={Iter}({Iterms:F1}ms), Switches={Sw}({Swms:F1}ms), MergeSets={Mergems:F1}ms, UpdateEdges={Edgems:F1}ms, Total={Total}ms - File={File}",
                function?.Name ?? currentClass?.Name ?? "<unknown>",
                basicBlockCount, timeInBasicBlocks * msPerTick,
                decisionCount, timeInDecisions * msPerTick,
                iterationCount, timeInIterations * msPerTick,
                switchCount, timeInSwitches * msPerTick,
                timeInMergeSets * msPerTick,
                timeInUpdateEdges * msPerTick,
                perfSw.ElapsedMilliseconds,
                FileName ?? "unknown");
        }
#endif

        // Check if we hit the iteration limit
        if (iterations >= maxIterations)
        {
            double iterationRatio = (double)iterations / Math.Max(1, totalNodes);
            if (iterationRatio > 80) // >80 iterations per node suggests genuine convergence issues
            {
                Log.Warning("Type flow analysis hit iteration limit ({MaxIterations}) for function {FunctionName} ({NodeCount} nodes, {Iterations} iterations, {IterationRatio:F1}x per node). This may indicate convergence issues.",
                    maxIterations, function?.Name ?? currentClass?.Name ?? "<anonymous>", totalNodes, iterations, iterationRatio);
            }
        }

        // Now that analysis is done, do one final pass to add diagnostics and sense tokens.
        Silent = false;
        Sense.SilentSenseTokens = false;

        foreach (CfgNode node in visited)
        {
            if (!InSets.TryGetValue(node, out Dictionary<string, ScrVariable>? inSet))
            {
                continue;
            }

            // Re-run analysis with Silent = false to generate diagnostics
            switch (node.Type)
            {
                case CfgNodeType.BasicBlock:
                    AnalyseBasicBlock((BasicBlock)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.EnumerationNode:
                    AnalyseEnumeration((EnumerationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.IterationNode:
                    AnalyseIteration((IterationNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.SwitchNode:
                    AnalyseSwitch((SwitchNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.SwitchCaseDecisionNode:
                    AnalyseSwitchCaseDecision((SwitchCaseDecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
                case CfgNodeType.DecisionNode:
                    AnalyseDecision((DecisionNode)node, new SymbolTable(ExportedSymbolTable, inSet, node.Scope, ApiData, currentClass, CurrentNamespace, KnownNamespaces));
                    break;
            }
        }
    }


    private void ValidateExpressionHasSideEffects(ExprNode expr)
    {
        // Check if this expression has side effects (similar to expression statement validation)
        bool hasSideEffects = expr.OperatorType switch
        {
            ExprOperatorType.Binary when expr is BinaryExprNode binaryExpr =>
                binaryExpr.Operation == TokenType.Assign ||
                binaryExpr.Operation == TokenType.PlusAssign ||
                binaryExpr.Operation == TokenType.MinusAssign ||
                binaryExpr.Operation == TokenType.MultiplyAssign ||
                binaryExpr.Operation == TokenType.DivideAssign ||
                binaryExpr.Operation == TokenType.ModuloAssign ||
                binaryExpr.Operation == TokenType.BitAndAssign ||
                binaryExpr.Operation == TokenType.BitOrAssign ||
                binaryExpr.Operation == TokenType.BitXorAssign ||
                binaryExpr.Operation == TokenType.BitLeftShiftAssign ||
                binaryExpr.Operation == TokenType.BitRightShiftAssign,

            ExprOperatorType.Postfix when expr is PostfixExprNode postfixExpr =>
                postfixExpr.Operator.Type == TokenType.Increment ||
                postfixExpr.Operator.Type == TokenType.Decrement,

            ExprOperatorType.FunctionCall => true,
            ExprOperatorType.MethodCall => true,
            ExprOperatorType.CallOn => true,

            _ => false
        };

        if (!hasSideEffects)
        {
            AddDiagnostic(expr.Range, GSCErrorCodes.InvalidExpressionStatement);
        }
    }

    public void AnalyseFunctionEntry(FunEntryBlock entry, Dictionary<string, ScrVariable> inSet)
    {
        FunDefnNode? node = entry.Source;

        // Handle constructor/destructor entries (which have null source)
        if (node is null)
        {
            return;
        }

        // Add the function's parameters to the in set.
        foreach (ParamNode param in node.Parameters.Parameters)
        {
            if (param.Name is null)
            {
                continue;
            }

            inSet[param.Name.Lexeme] = new(param.Name.Lexeme, ScrData.Default, 0, false);
        }

        // Note: Built-in globals (self, level, game, anim) are no longer added to the symbol table.
        // They are implicitly available but should be handled separately if needed.

        if (node.Parameters.Vararg)
        {
            inSet["vararg"] = new("vararg", new ScrData(ScrDataTypes.Array), 0, true);
        }
    }

    public void AnalyseEnumeration(EnumerationNode node, SymbolTable symbolTable)
    {
        ForeachStmtNode foreachStmt = node.Source;

        // Nothing to work with, errored earlier on.
        if (foreachStmt.Collection is null)
        {
            return;
        }

        // Analyse the collection.
        ScrData collection = AnalyseExpr(foreachStmt.Collection, symbolTable, Sense);

        if (!collection.IsCompatibleWith(ScrDataTypes.Array))
        {
            AddDiagnostic(foreachStmt.Collection.Range, GSCErrorCodes.CannotEnumerateType, collection.TypeToString());
        }

        if (foreachStmt.KeyIdentifier is not null)
        {
            Token keyIdentifier = foreachStmt.KeyIdentifier.Token;
            AssignmentResult keyAssignmentResult = symbolTable.AddOrSetVariableSymbol(keyIdentifier.Lexeme, ScrData.Default, definitionSource: foreachStmt);

            if (keyAssignmentResult == AssignmentResult.SuccessNew)
            {
                Sense.AddSenseToken(keyIdentifier, ScrVariableSymbol.Declaration(foreachStmt.KeyIdentifier, ScrData.Default));
            }
        }

        Token valueIdentifier = foreachStmt.ValueIdentifier.Token;
        AssignmentResult valueAssignmentResult = symbolTable.AddOrSetVariableSymbol(valueIdentifier.Lexeme, ScrData.Default, definitionSource: foreachStmt);

        // if (!assignmentResult)
        // {
        //     // TODO: how does GSC handle this?
        // }
        if (valueAssignmentResult == AssignmentResult.SuccessNew)
        {
            Sense.AddSenseToken(valueIdentifier, ScrVariableSymbol.Declaration(foreachStmt.ValueIdentifier, ScrData.Default));
        }
        else if (valueAssignmentResult == AssignmentResult.SuccessMutated)
        {
            Sense.AddSenseToken(valueIdentifier, ScrVariableSymbol.Usage(foreachStmt.ValueIdentifier, ScrData.Default));
        }
    }

    public void AnalyseIteration(IterationNode node, SymbolTable symbolTable)
    {
        _ = AnalyseIterationInternal(node, symbolTable);
    }

    public void AnalyseDecision(DecisionNode node, SymbolTable symbolTable)
    {
        _ = AnalyseDecisionConditionInternal(node, symbolTable);
    }

    private ConditionResult? AnalyseIterationInternal(IterationNode node, SymbolTable symbolTable)
    {
        // Analyse the initialisation.
        if (node.Initialisation is not null)
        {
            AnalyseExpr(node.Initialisation, symbolTable, Sense);

            // For loop initialization should follow the same rules as expression statements
            // Only assignments, calls, increments, decrements should be allowed
            ValidateExpressionHasSideEffects(node.Initialisation);
        }

        ConditionResult? conditionResult = null;

        // Analyse the condition if present
        if (node.Condition is not null)
        {
            conditionResult = AnalyseCondition(node.Condition, symbolTable);
            ScrData conditionValue = conditionResult.Value.Value;

            if (!conditionValue.TypeUnknown() && !conditionValue.CanEvaluateToBoolean())
            {
                AddDiagnostic(node.Condition.Range, GSCErrorCodes.NoImplicitConversionExists, conditionValue.TypeToString(), ScrDataTypeNames.Bool);
            }
        }

        // Analyse the increment if present
        if (node.Increment is not null)
        {
            AnalyseExpr(node.Increment, symbolTable, Sense);

            // For loop increment should also follow expression statement rules
            ValidateExpressionHasSideEffects(node.Increment);
        }

        return conditionResult;
    }

    private ConditionResult? AnalyseDecisionConditionInternal(DecisionNode node, SymbolTable symbolTable)
    {
        DecisionAstNode decision = node.Source;

        // It either errored or it's an else, nothing to do.
        if (decision.Condition is null)
        {
            return null;
        }

        ConditionResult conditionResult = AnalyseCondition(decision.Condition, symbolTable);
        ScrData conditionValue = conditionResult.Value;

        if (!conditionValue.TypeUnknown() && !conditionValue.CanEvaluateToBoolean())
        {
            AddDiagnostic(decision.Condition.Range, GSCErrorCodes.NoImplicitConversionExists, conditionValue.TypeToString(), ScrDataTypeNames.Bool);
        }

        // TODO: if the condition evaluates to false, then mark the block that follows as unreachable.
        // TODO: if the condition evaluates to true, then any else blocks are unreachable.
        return conditionResult;
    }

    public void AnalyseSwitch(SwitchNode node, SymbolTable symbolTable)
    {
        // Create context for this switch (only once, even if revisited)
        if (!SwitchContexts.ContainsKey(node))
        {
            var context = new SwitchAnalysisContext();

            // Analyze expression ONCE and cache it
            if (node.Source.Expression is not null)
            {
                context.SwitchExpressionType = AnalyseExpr(node.Source.Expression, symbolTable, Sense);
            }

            SwitchContexts[node] = context;
        }
    }

    public void AnalyseSwitchCaseDecision(SwitchCaseDecisionNode node, SymbolTable symbolTable)
    {
        // Look up the switch context (guaranteed to exist because worklist processes SwitchNode first)
        if (!SwitchContexts.TryGetValue(node.ParentSwitch, out var context))
        {
            return; // Defensive: shouldn't happen
        }

        // Check if we've already analyzed this specific node's labels
        bool isFirstTimeAnalyzingThisNode = context.AnalyzedNodes.Add(node);

        ScrData switchType = context.SwitchExpressionType;

        foreach (CaseLabelNode label in node.Labels)
        {
            if (label.NodeType == AstNodeType.DefaultLabel)
            {
                // Only update state on first analysis of this node
                if (isFirstTimeAnalyzingThisNode)
                {
                    if (context.HasDefault)
                    {
                        // Duplicate default (already caught in CFG construction, but could warn again if needed)
                    }
                    context.HasDefault = true;
                }
                continue;
            }

            if (label.Value is null) continue;

            // Analyze the label value
            ScrData labelType = AnalyseExpr(label.Value, symbolTable, Sense);

            // Type compatibility check - always check since types can change during analysis
            if (!AreTypesCompatibleForSwitch(switchType, labelType))
            {
                if (!Silent) // Only emit in diagnostic pass
                {
                    AddDiagnostic(label.Value.Range, GSCErrorCodes.UnreachableCase);
                }
            }

            // TODO: this isn't working at the moment.
            // Duplicate label check - only on first analysis of this node
            if (isFirstTimeAnalyzingThisNode && TryGetCaseLabelValueKey(label.Value, out string key))
            {
                if (!context.SeenLabelValues.Add(key))
                {
                    if (!Silent) // Only emit in diagnostic pass
                    {
                        AddDiagnostic(label.Value.Range, GSCErrorCodes.DuplicateCaseLabel);
                    }
                }
            }
        }
    }

    private bool AreTypesCompatibleForSwitch(ScrData switchType, ScrData labelType)
    {
        // If either type is unknown, assume compatible
        if (switchType.TypeUnknown() || labelType.TypeUnknown())
        {
            return true;
        }

        // TODO: Implement proper type compatibility rules for switch statements
        // For now, allow any comparison (GSC is weakly typed)
        return true;
    }

    private bool TryGetCaseLabelValueKey(ExprNode expr, out string key)
    {
        key = string.Empty;

        if (expr is DataExprNode dataExpr)
        {
            // Encode type in key to avoid collisions between e.g., string "1" and int 1
            key = dataExpr.Type switch
            {
                ScrDataTypes.Int => $"int:{dataExpr.Value}",
                ScrDataTypes.Float => $"float:{dataExpr.Value}",
                ScrDataTypes.String => $"str:{dataExpr.Value}",
                ScrDataTypes.IString => $"istr:{dataExpr.Value}",
                ScrDataTypes.Hash => $"hash:{dataExpr.Value}",
                ScrDataTypes.Bool => $"bool:{dataExpr.Value}",
                _ => string.Empty
            };
            return key.Length > 0;
        }

        return false;
    }

    public void AnalyseBasicBlock(BasicBlock block, SymbolTable symbolTable)
    {
        LinkedList<AstNode> logic = block.Statements;

        if (logic.Count == 0)
        {
            return;
        }

        for (LinkedListNode<AstNode>? node = logic.First; node != null; node = node.Next)
        {
            AstNode child = node.Value;

            AstNode? last = node.Previous?.Value;
            AstNode? next = node.Next?.Value;

            AnalyseStatement(child, last, next, symbolTable);
        }
    }

    public void AnalyseStatement(AstNode statement, AstNode? last, AstNode? next, SymbolTable symbolTable)
    {
        switch (statement.NodeType)
        {
            case AstNodeType.ExprStmt:
                AnalyseExprStmt((ExprStmtNode)statement, last, next, symbolTable);
                break;
            case AstNodeType.ConstStmt:
                AnalyseConstStmt((ConstStmtNode)statement, last, next, symbolTable);
                break;
            case AstNodeType.ReturnStmt:
                AnalyseReturnStmt((ReturnStmtNode)statement, symbolTable);
                break;
            case AstNodeType.WaitStmt:
                AnalyseWaitStmt((ReservedFuncStmtNode)statement, symbolTable);
                break;
            case AstNodeType.WaitRealTimeStmt:
                AnalyseWaitStmt((ReservedFuncStmtNode)statement, symbolTable);
                break;
            default:
                break;
        }
    }

    public void AnalyseExprStmt(ExprStmtNode statement, AstNode? last, AstNode? next, SymbolTable symbolTable)
    {
        if (statement.Expr is null)
        {
            return;
        }

        ScrData result = AnalyseExpr(statement.Expr, symbolTable, Sense);

        // If this is an assert() call, apply type narrowings from the condition being true.
        if (statement.Expr is FunCallNode { Function: IdentifierExprNode assertId } assertCall
            && string.Equals(assertId.Identifier, "assert", StringComparison.OrdinalIgnoreCase)
            && assertCall.Arguments.Arguments.Count == 1
            && assertCall.Arguments.Arguments.First?.Value is ExprNode conditionExpr)
        {
            ConditionResult condition = AnalyseCondition(conditionExpr, symbolTable);
            ApplyNarrowingsToSymbolTable(symbolTable, condition.WhenTrue);
        }
    }

    public void AnalyseConstStmt(ConstStmtNode statement, AstNode? last, AstNode? next, SymbolTable symbolTable)
    {
        if (statement.Value is null)
        {
            return;
        }

        ScrData result = AnalyseExpr(statement.Value, symbolTable, Sense);

        // Validate that the RHS is a compile-time constant
        if (!IsConstantExpression(statement.Value))
        {
            AddDiagnostic(statement.Value!.Range, GSCErrorCodes.ExpectedConstantExpression);
            return;
        }

        // Assign the result to the symbol table.
        AssignmentResult assignmentResult = symbolTable.TryAddVariableSymbol(statement.Identifier, result, isConstant: true, sourceLocation: statement.IdentifierToken.Range, definitionSource: statement);

        if (assignmentResult == AssignmentResult.SuccessNew || assignmentResult == AssignmentResult.SuccessAlreadyDefined)
        {
            // Add a semantic token for the constant.
            Sense.AddSenseToken(statement.IdentifierToken, ScrVariableSymbol.ConstantDeclaration(statement.IdentifierToken, result));
            return;
        }
        else if (assignmentResult == AssignmentResult.FailedReserved)
        {
            AddDiagnostic(statement.Range, GSCErrorCodes.ReservedSymbol, statement.Identifier);
            return;
        }

        AddDiagnostic(statement.Range, GSCErrorCodes.RedefinitionOfSymbol, statement.Identifier);
    }

    public void AnalyseReturnStmt(ReturnStmtNode statement, SymbolTable symbolTable)
    {
        // If there's a return value, analyze it
        if (statement.Value is not null)
        {
            ScrData result = AnalyseExpr(statement.Value, symbolTable, Sense);
            // TODO: Could validate return type matches function signature if available
        }
    }

    public void AnalyseWaitStmt(ReservedFuncStmtNode statement, SymbolTable symbolTable)
    {
        // Wait/WaitRealTime statements must have a duration expression
        if (statement.Expr is null)
        {
            return;
        }

        ScrData duration = AnalyseExpr(statement.Expr, symbolTable, Sense);

        // Duration must be numeric (int or float) or any
        if (!duration.IsCompatibleWith(ScrDataTypes.Number))
        {
            AddDiagnostic(statement.Expr.Range, GSCErrorCodes.NoImplicitConversionExists,
                duration.TypeToString(), ScrDataTypeNames.Number);
        }
    }

    private bool UpdateOutEdgeSetsForNode(CfgNode node, Dictionary<string, ScrVariable> baseOut, ConditionResult? condition)
    {
        bool anyChanged = false;

        foreach (CfgNode successor in node.Outgoing)
        {
            Dictionary<string, ScrVariable> edgeOut;

            if (condition is not null && node is DecisionNode decisionNode)
            {
                if (successor == decisionNode.WhenTrue)
                {
                    edgeOut = RefineVariableSymbols(baseOut, condition.Value.WhenTrue);
                }
                else if (successor == decisionNode.WhenFalse)
                {
                    edgeOut = RefineVariableSymbols(baseOut, condition.Value.WhenFalse);
                }
                else
                {
                    edgeOut = CloneVariableSymbols(baseOut);
                }
            }
            else if (condition is not null && node is IterationNode iterationNode)
            {
                if (successor == iterationNode.Body)
                {
                    edgeOut = RefineVariableSymbols(baseOut, condition.Value.WhenTrue);
                }
                else if (successor == iterationNode.Continuation)
                {
                    edgeOut = RefineVariableSymbols(baseOut, condition.Value.WhenFalse);
                }
                else
                {
                    edgeOut = CloneVariableSymbols(baseOut);
                }
            }
            else
            {
                edgeOut = CloneVariableSymbols(baseOut);
            }

            (CfgNode From, CfgNode To) edge = (node, successor);

            if (OutEdgeSets.TryGetValue(edge, out Dictionary<string, ScrVariable>? previousEdgeOut))
            {
                if (!previousEdgeOut.VariableTableEquals(edgeOut))
                {
                    anyChanged = true;
                }
            }
            else
            {
                anyChanged = true;
            }

            OutEdgeSets[edge] = edgeOut;
        }

        return anyChanged;
    }

    private static Dictionary<string, ScrVariable> CloneVariableSymbols(Dictionary<string, ScrVariable> symbols)
    {
        return new Dictionary<string, ScrVariable>(symbols, StringComparer.OrdinalIgnoreCase);
    }

    private Dictionary<string, ScrVariable> RefineVariableSymbols(Dictionary<string, ScrVariable> baseOut, Dictionary<string, TypeNarrowing> narrowings)
    {
        if (narrowings.Count == 0)
        {
            return CloneVariableSymbols(baseOut);
        }

        Dictionary<string, ScrVariable> refined = CloneVariableSymbols(baseOut);

        foreach ((string symbol, TypeNarrowing narrowing) in narrowings)
        {
            if (!refined.TryGetValue(symbol, out ScrVariable? existing) || existing is null)
            {
                continue;
            }

            refined[symbol] = existing with { Data = ApplyNarrowing(narrowing, existing.Data) };
        }

        return refined;
    }

    public void AddDiagnostic(Range range, GSCErrorCodes code, params object[] args)
    {
        // Only issue the diagnostics on the final pass.
        if (Silent)
        {
            return;
        }
        Sense.AddSpaDiagnostic(range, code, args);
    }

    private static int CountAllNodes(ControlFlowGraph graph)
    {
        HashSet<CfgNode> visited = new();
        Stack<CfgNode> stack = new();
        stack.Push(graph.Start);

        while (stack.Count > 0)
        {
            CfgNode node = stack.Pop();
            if (visited.Add(node))
            {
                foreach (CfgNode successor in node.Outgoing)
                {
                    if (!visited.Contains(successor))
                    {
                        stack.Push(successor);
                    }
                }
            }
        }

        return visited.Count;
    }

    /// <summary>
    /// Determines if an expression is a compile-time constant that can be used in a const declaration.
    /// This includes literal values and expressions that can be evaluated at compile time.
    /// </summary>
    /// <param name="expr">The expression to check</param>
    /// <returns>True if the expression is a compile-time constant, false otherwise</returns>
    private static bool IsConstantExpression(ExprNode? expr)
    {
        if (expr == null)
            return false;

        return expr.OperatorType switch
        {
            // Literal values are always compile-time constants
            ExprOperatorType.DataOperand => true,

            // Binary expressions can be constants if both operands are constants
            ExprOperatorType.Binary when expr is BinaryExprNode binaryExpr => 
                IsCompileTimeConstantBinaryOp(binaryExpr.Operation) &&
                IsConstantExpression(binaryExpr.Left) && 
                IsConstantExpression(binaryExpr.Right),

            // Unary expressions can be constants if the operand is constant
            ExprOperatorType.Prefix when expr is PrefixExprNode prefixExpr =>
                IsCompileTimeConstantPrefixOp(prefixExpr.Operation) &&
                IsConstantExpression(prefixExpr.Operand),

            // Ternary expressions can be constants if all parts are constants
            ExprOperatorType.Ternary when expr is TernaryExprNode ternaryExpr =>
                IsConstantExpression(ternaryExpr.Condition) &&
                IsConstantExpression(ternaryExpr.Then) &&
                IsConstantExpression(ternaryExpr.Else),

            // Vector expressions can be constants if all components are constants
            ExprOperatorType.Vector when expr is VectorExprNode vectorExpr =>
                IsConstantExpression(vectorExpr.X) &&
                IsConstantExpression(vectorExpr.Y) &&
                IsConstantExpression(vectorExpr.Z),

            // All other expression types involve runtime evaluation
            _ => false
        };
    }

    /// <summary>
    /// Determines if a binary operator can be evaluated at compile time.
    /// </summary>
    private static bool IsCompileTimeConstantBinaryOp(TokenType operation)
    {
        return operation switch
        {
            // Arithmetic operators
            TokenType.Plus => true,
            TokenType.Minus => true,
            TokenType.Multiply => true,
            TokenType.Divide => true,
            TokenType.Modulo => true,
            
            // Bitwise operators
            TokenType.BitAnd => true,
            TokenType.BitOr => true,
            TokenType.BitXor => true,
            TokenType.BitLeftShift => true,
            TokenType.BitRightShift => true,
            
            // Logical operators
            TokenType.And => true,
            TokenType.Or => true,
            
            // Comparison operators
            TokenType.Equals => true,
            TokenType.NotEquals => true,
            TokenType.LessThan => true,
            TokenType.LessThanEquals => true,
            TokenType.GreaterThan => true,
            TokenType.GreaterThanEquals => true,
            
            // Assignment and other runtime operators are not compile-time constants
            _ => false
        };
    }

    /// <summary>
    /// Determines if a prefix operator can be evaluated at compile time.
    /// </summary>
    private static bool IsCompileTimeConstantPrefixOp(TokenType operation)
    {
        return operation switch
        {
            // Unary arithmetic operators
            TokenType.Plus => true,
            TokenType.Minus => true,
            TokenType.BitNot => true,
            TokenType.Not => true,
            
            // Runtime operators like increment/decrement are not compile-time constants
            _ => false
        };
    }

}


file static class DataFlowAnalyserExtensions
{
    public static void MergeTables(this Dictionary<string, ScrVariable> target, Dictionary<string, ScrVariable> source, int maxScope)
    {
        // Iterate source only — keys only in target stay as-is (nothing to merge from source).
        // This eliminates the HashSet allocation + union that the old implementation used.
        foreach (var kvp in source)
        {
            if (kvp.Value.LexicalScope > maxScope) continue;

            if (target.TryGetValue(kvp.Key, out ScrVariable? targetData))
            {
                if (kvp.Value != targetData)
                {
                    target[kvp.Key] = kvp.Value with
                    {
                        Data = ScrData.Merge(targetData.Data, kvp.Value.Data),
                        IsConstant = kvp.Value.IsConstant && targetData.IsConstant,
                        SourceLocation = kvp.Value.SourceLocation ?? targetData.SourceLocation
                    };
                }
            }
            else
            {
                target[kvp.Key] = kvp.Value with { Data = kvp.Value.Data.Copy() };
            }
        }
    }

    public static bool VariableTableEquals(this Dictionary<string, ScrVariable> target, Dictionary<string, ScrVariable> source)
    {
        if (target.Count != source.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, ScrVariable> pair in target)
        {
            if (!source.TryGetValue(pair.Key, out ScrVariable? value))
            {
                return false;
            }

            // Compare only semantically relevant fields for convergence.
            // SourceLocation and DefinitionSource are metadata that can oscillate
            // depending on merge order at join points, preventing convergence.
            var a = pair.Value;
            var b = value;
            if (a.Data != b.Data ||
                a.LexicalScope != b.LexicalScope ||
                a.Global != b.Global ||
                a.IsConstant != b.IsConstant)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes an order-independent hash of a variable table for fast convergence checks.
    /// Uses XOR so iteration order doesn't matter. Only hashes semantically relevant fields
    /// (matching VariableTableEquals).
    /// </summary>
    public static int ComputeTableHash(this Dictionary<string, ScrVariable> table)
    {
        int hash = table.Count;
        foreach (var pair in table)
        {
            var v = pair.Value;
            int entryHash = StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Key);
            entryHash = entryHash * 397 ^ v.Data.GetHashCode();
            entryHash = entryHash * 397 ^ v.LexicalScope;
            entryHash = entryHash * 397 ^ (v.Global ? 1 : 0);
            entryHash = entryHash * 397 ^ (v.IsConstant ? 1 : 0);
            hash ^= entryHash;
        }
        return hash;
    }
}