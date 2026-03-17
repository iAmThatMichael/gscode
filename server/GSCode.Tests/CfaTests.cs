using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.Lexical;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Tests;

public class CfaTests
{
    [Fact]
    public void Test_BasicFunction()
    {
        // Expectation:
        // (entry) -> (logic) -> (exit)

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new ExprStmtNode(null),
                new ExprStmtNode(null),
                new ExprStmtNode(null),
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        Assert.NotNull(cfg.Start);
        Assert.NotNull(cfg.End);

        CfgNode outgoing = Assert.Single(cfg.Start.Outgoing);
        CfgNode incoming = Assert.Single(cfg.End.Incoming);

        Assert.IsType<BasicBlock>(outgoing);
        Assert.Equal(outgoing, incoming);
    }

    [Fact]
    public void Test_SimpleIf()
    {
        // Expectation:
        // (entry) -> (logic) -> (if)   -> (then) -> (logic_cont) -> (exit)
        //                              --------------^

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new ExprStmtNode(null),
                new IfStmtNode(DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 1))))
                {
                    Then = new ExprStmtNode(null),
                    Else = null,
                },
                new ExprStmtNode(null),
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        Assert.NotNull(cfg.Start);
        Assert.NotNull(cfg.End);

        CfgNode outgoing = Assert.Single(cfg.Start.Outgoing);

        Assert.IsType<BasicBlock>(outgoing);
        CfgNode ifBlock = Assert.Single(outgoing.Outgoing);
        DecisionNode decision = Assert.IsType<DecisionNode>(ifBlock);

        Assert.IsType<BasicBlock>(decision.WhenTrue);
        Assert.IsType<BasicBlock>(decision.WhenFalse);

        CfgNode outgoingTrue = Assert.Single(decision.WhenTrue.Outgoing);
        Assert.Equal(outgoingTrue, decision.WhenFalse);
    }

    [Fact]
    public void Test_SimpleIfElse()
    {
        // Expectation:
        // (entry) -> (logic) -> (if) --(true)--> (then) --\
        //                              --(false)-> (else) --+--> (logic_cont) -> (exit)

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new ExprStmtNode(null), // logic
                new IfStmtNode(DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 1))))
                {
                    Then = new ExprStmtNode(null), // then
                    Else = new IfStmtNode(null)
                    {
                        Then = new ExprStmtNode(null), // then
                        Else = null, // else
                    },
                },
                new ExprStmtNode(null), // logic_cont
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        Assert.NotNull(cfg.Start);
        Assert.NotNull(cfg.End);

        CfgNode preIfBlock = Assert.Single(cfg.Start.Outgoing); // logic
        Assert.IsType<BasicBlock>(preIfBlock);

        CfgNode ifBlock = Assert.Single(preIfBlock.Outgoing); // if
        DecisionNode decision = Assert.IsType<DecisionNode>(ifBlock);

        Assert.NotNull(decision.WhenTrue); // then
        BasicBlock thenBlock = Assert.IsType<BasicBlock>(decision.WhenTrue);

        Assert.NotNull(decision.WhenFalse); // else
        BasicBlock elseBlock = Assert.IsType<BasicBlock>(decision.WhenFalse);

        CfgNode afterThen = Assert.Single(thenBlock.Outgoing); // logic_cont
        CfgNode afterElse = Assert.Single(elseBlock.Outgoing); // logic_cont

        Assert.Equal(afterThen, afterElse); // Common continuation point
        Assert.IsType<BasicBlock>(afterThen);

        CfgNode exitNode = Assert.Single(afterThen.Outgoing);
        Assert.Equal(cfg.End, exitNode);
    }

    [Fact]
    public void Test_SimpleSwitch()
    {
        // Expectation:
        // (entry) -> (switch) -> (case1Decision) -> (case2Decision) -> (continuation/exit)
        //                        case1Decision (whenTrue) -> body1 -> (continuation)
        //                        case2Decision (whenTrue) -> body2 -> (continuation)

        CaseStmtNode case1 = CreateCase(1, [new ExprStmtNode(null), BreakStmt()]);
        CaseStmtNode case2 = CreateCase(2, [new ExprStmtNode(null), BreakStmt()]);

        SwitchStmtNode switchStmt = CreateSwitch([case1, case2]);

        ControlFlowGraph cfg = BuildSwitchCfg(switchStmt);

        // entry -> switch node
        SwitchNode switchNode = GetSingleOutgoing<SwitchNode>(cfg.Start);

        // switch -> case1 decision (contains all labels for case 1)
        SwitchCaseDecisionNode case1Decision = GetSingleOutgoing<SwitchCaseDecisionNode>(switchNode);

        // case1 -> case2 decision (when false)
        SwitchCaseDecisionNode case2Decision = Assert.IsType<SwitchCaseDecisionNode>(case1Decision.WhenFalse);

        // case1 -> body1 (when true)
        BasicBlock body1 = Assert.IsType<BasicBlock>(case1Decision.WhenTrue);

        // case2 -> body2 (when true)
        BasicBlock body2 = Assert.IsType<BasicBlock>(case2Decision.WhenTrue);

        // Both bodies should have break statements, so they connect to continuation
        CfgNode contFromBody1 = Assert.Single(body1.Outgoing);
        CfgNode contFromBody2 = Assert.Single(body2.Outgoing);

        // Both should connect to the same continuation point
        Assert.Equal(contFromBody1, contFromBody2);

        // case2's WhenFalse should also point to continuation (last case, no default)
        Assert.Equal(case2Decision.WhenFalse, contFromBody1);

        // Continuation should be a basic block (the statement after switch)
        BasicBlock continuation = Assert.IsType<BasicBlock>(contFromBody1);

        // Continuation should connect to exit
        CfgNode exitNode = Assert.Single(continuation.Outgoing);
        Assert.Equal(cfg.End, exitNode);
    }

    private StmtListNode StmtListNodeFromList(List<AstNode> statements)
    {
        LinkedList<AstNode> list = new();
        foreach (AstNode statement in statements)
        {
            list.AddLast(statement);
        }

        return new StmtListNode(list);
    }

    // Switch test helpers

    /// <summary>
    /// Creates a case statement with a single case label and the given body statements.
    /// </summary>
    private CaseStmtNode CreateCase(int labelValue, List<AstNode> bodyStatements)
    {
        CaseStmtNode caseStmt = new()
        {
            Body = StmtListNodeFromList(bodyStatements)
        };
        caseStmt.Labels.AddLast(CreateCaseLabel(labelValue));
        return caseStmt;
    }

    /// <summary>
    /// Creates a default case statement with the given body statements.
    /// </summary>
    private CaseStmtNode CreateDefaultCase(List<AstNode> bodyStatements)
    {
        CaseStmtNode caseStmt = new()
        {
            Body = StmtListNodeFromList(bodyStatements)
        };
        caseStmt.Labels.AddLast(CreateDefaultLabel());
        return caseStmt;
    }

    /// <summary>
    /// Creates a case statement with multiple labels and the given body statements.
    /// </summary>
    private CaseStmtNode CreateCaseWithMultipleLabels(List<int> labelValues, List<AstNode> bodyStatements)
    {
        CaseStmtNode caseStmt = new()
        {
            Body = StmtListNodeFromList(bodyStatements)
        };
        foreach (int value in labelValues)
        {
            caseStmt.Labels.AddLast(CreateCaseLabel(value));
        }
        return caseStmt;
    }

    /// <summary>
    /// Creates a case label node with an integer value.
    /// </summary>
    private CaseLabelNode CreateCaseLabel(int value)
    {
        return new CaseLabelNode(
            AstNodeType.CaseLabel,
            new Token(TokenType.Case, new TokenRange(0, 0, 0, 4)),
            DataExprNode.From(new LexemeToken(TokenType.Integer, new TokenRange(0, 0, 0, 1), value.ToString()))
        );
    }

    /// <summary>
    /// Creates a default label node.
    /// </summary>
    private CaseLabelNode CreateDefaultLabel()
    {
        return new CaseLabelNode(
            AstNodeType.DefaultLabel,
            new Token(TokenType.Default, new TokenRange(0, 0, 0, 7)),
            null
        );
    }

    /// <summary>
    /// Creates a switch statement from a list of cases.
    /// </summary>
    private SwitchStmtNode CreateSwitch(List<CaseStmtNode> cases, ExprNode? expression = null)
    {
        CaseListNode caseList = new();
        foreach (CaseStmtNode caseStmt in cases)
        {
            caseList.Cases.AddLast(caseStmt);
        }

        return new SwitchStmtNode()
        {
            Expression = expression ?? DataExprNode.From(new LexemeToken(TokenType.Integer, new TokenRange(0, 0, 0, 1), "1")),
            Cases = caseList
        };
    }

    /// <summary>
    /// Builds a CFG for a function containing a switch statement and a continuation statement.
    /// </summary>
    private ControlFlowGraph BuildSwitchCfg(SwitchStmtNode switchStmt)
    {
        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                switchStmt,
                new ExprStmtNode(null), // continuation after switch
            ])
        };

        return ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));
    }

    // ===== Additional CFA tests =====

    [Fact]
    public void Test_WhileLoop()
    {
        // Expectation:
        // (entry) -> (while decision) -> (body) -> back to (while decision)
        //                              -> (continuation) -> (exit)

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new WhileStmtNode(
                    DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 4))),
                    new ExprStmtNode(null)
                ),
                new ExprStmtNode(null), // continuation
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        // entry -> while decision
        DecisionNode decision = GetSingleOutgoing<DecisionNode>(cfg.Start);

        // decision.WhenTrue -> body
        Assert.NotNull(decision.WhenTrue);
        BasicBlock body = Assert.IsType<BasicBlock>(decision.WhenTrue);

        // body loops back to decision
        CfgNode loopBack = Assert.Single(body.Outgoing);
        Assert.Equal(decision, loopBack);

        // decision.WhenFalse -> continuation
        Assert.NotNull(decision.WhenFalse);
        BasicBlock continuation = Assert.IsType<BasicBlock>(decision.WhenFalse);

        CfgNode exitNode = Assert.Single(continuation.Outgoing);
        Assert.Equal(cfg.End, exitNode);
    }

    [Fact]
    public void Test_ForLoop()
    {
        // Expectation:
        // (entry) -> (iteration) -> (body) -> back to (iteration)
        //                         -> (continuation) -> (exit)

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new ForStmtNode(
                    null, // init
                    DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 4))), // condition
                    null, // increment
                    new ExprStmtNode(null) // body
                ),
                new ExprStmtNode(null), // continuation
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        // entry -> iteration node
        IterationNode iteration = GetSingleOutgoing<IterationNode>(cfg.Start);

        Assert.NotNull(iteration.Body);
        Assert.NotNull(iteration.Continuation);

        // body loops back to iteration
        BasicBlock body = Assert.IsType<BasicBlock>(iteration.Body);
        CfgNode loopBack = Assert.Single(body.Outgoing);
        Assert.Equal(iteration, loopBack);

        // continuation leads to exit
        BasicBlock continuation = Assert.IsType<BasicBlock>(iteration.Continuation);
        CfgNode exitNode = Assert.Single(continuation.Outgoing);
        Assert.Equal(cfg.End, exitNode);
    }

    [Fact]
    public void Test_DoWhile()
    {
        // Expectation:
        // (entry) -> (body) -> (decision) -> (body) [loop]
        //                                 -> (continuation) -> (exit)

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new DoWhileStmtNode(
                    DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 4))),
                    new ExprStmtNode(null)
                ),
                new ExprStmtNode(null), // continuation
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        // entry -> body first (do-while executes body before condition)
        BasicBlock body = GetSingleOutgoing<BasicBlock>(cfg.Start);

        // body -> decision
        DecisionNode decision = GetSingleOutgoing<DecisionNode>(body);

        // decision.WhenTrue loops back to body
        Assert.NotNull(decision.WhenTrue);
        Assert.Equal(body, decision.WhenTrue);

        // decision.WhenFalse -> continuation
        Assert.NotNull(decision.WhenFalse);
        BasicBlock continuation = Assert.IsType<BasicBlock>(decision.WhenFalse);

        CfgNode exitNode = Assert.Single(continuation.Outgoing);
        Assert.Equal(cfg.End, exitNode);
    }

    [Fact]
    public void Test_NestedIfElse()
    {
        // Tests that nested if-else blocks have correct merge points

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new IfStmtNode(DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 1))))
                {
                    Then = new IfStmtNode(DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 1))))
                    {
                        Then = new ExprStmtNode(null),
                        Else = new IfStmtNode(null)
                        {
                            Then = new ExprStmtNode(null),
                            Else = null,
                        },
                    },
                    Else = new IfStmtNode(null)
                    {
                        Then = new ExprStmtNode(null),
                        Else = null,
                    },
                },
                new ExprStmtNode(null), // final continuation
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        Assert.NotNull(cfg.Start);
        Assert.NotNull(cfg.End);

        // Outer if decision
        DecisionNode outerDecision = GetSingleOutgoing<DecisionNode>(cfg.Start);
        Assert.NotNull(outerDecision.WhenTrue);
        Assert.NotNull(outerDecision.WhenFalse);
    }

    [Fact]
    public void Test_BreakInLoop()
    {
        // while(true) { break; }
        // continuation

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new WhileStmtNode(
                    DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 4))),
                    StmtListNodeFromList([
                        BreakStmt(),
                    ])
                ),
                new ExprStmtNode(null), // continuation after loop
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        // entry -> while decision
        DecisionNode decision = GetSingleOutgoing<DecisionNode>(cfg.Start);

        // WhenTrue -> body with break
        Assert.NotNull(decision.WhenTrue);
        BasicBlock body = Assert.IsType<BasicBlock>(decision.WhenTrue);

        // Break should jump to continuation (same as WhenFalse)
        CfgNode breakTarget = Assert.Single(body.Outgoing);
        Assert.Equal(decision.WhenFalse, breakTarget);
    }

    [Fact]
    public void Test_ContinueInLoop()
    {
        // while(true) { continue; }
        // continuation

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new WhileStmtNode(
                    DataExprNode.From(new Token(TokenType.True, new TokenRange(0, 0, 0, 4))),
                    StmtListNodeFromList([
                        ContinueStmt(),
                    ])
                ),
                new ExprStmtNode(null),
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        // entry -> while decision
        DecisionNode decision = GetSingleOutgoing<DecisionNode>(cfg.Start);

        // WhenTrue -> body with continue
        Assert.NotNull(decision.WhenTrue);
        BasicBlock body = Assert.IsType<BasicBlock>(decision.WhenTrue);

        // Continue should jump back to decision (loop head)
        CfgNode continueTarget = Assert.Single(body.Outgoing);
        Assert.Equal(decision, continueTarget);
    }

    // ===== Helpers =====

    /// <summary>
    /// Creates a break statement node.
    /// </summary>
    private ControlFlowActionNode BreakStmt()
    {
        return new ControlFlowActionNode(
            AstNodeType.BreakStmt,
            new Token(TokenType.Break, new TokenRange(0, 0, 0, 5))
        );
    }

    /// <summary>
    /// Creates a continue statement node.
    /// </summary>
    private ControlFlowActionNode ContinueStmt()
    {
        return new ControlFlowActionNode(
            AstNodeType.ContinueStmt,
            new Token(TokenType.Continue, new TokenRange(0, 0, 0, 8))
        );
    }

    /// <summary>
    /// Gets the single outgoing node and asserts it's of the expected type.
    /// </summary>
    private T GetSingleOutgoing<T>(CfgNode node) where T : CfgNode
    {
        CfgNode outgoing = Assert.Single(node.Outgoing);
        return Assert.IsType<T>(outgoing);
    }
}
