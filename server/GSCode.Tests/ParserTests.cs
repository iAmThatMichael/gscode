using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using Xunit;

namespace GSCode.Tests;

public class ParserTests
{
    private static ScriptNode Parse(string source)
    {
        TokenList tokens = TestHelper.Lex(source);
        var sense = TestHelper.CreateDummySense();
        GSCode.Parser.AST.Parser parser = new(tokens.Start!, sense, "gsc");
        return parser.Parse();
    }

    [Fact]
    public void Parse_EmptyFunction()
    {
        ScriptNode script = Parse("function test() {}");

        Assert.Single(script.ScriptDefns);
        Assert.IsType<FunDefnNode>(script.ScriptDefns[0]);
    }

    [Fact]
    public void Parse_FunctionWithAssignment()
    {
        ScriptNode script = Parse("function test() { x = 5; }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        Assert.NotNull(fn.Body);
        Assert.NotEmpty(fn.Body.Statements);
        Assert.IsType<ExprStmtNode>(fn.Body.Statements.First!.Value);
    }

    [Fact]
    public void Parse_IfStatement()
    {
        ScriptNode script = Parse("function test() { if(true) { x = 1; } }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        Assert.Contains(fn.Body.Statements, n => n is IfStmtNode);
    }

    [Fact]
    public void Parse_IfElseStatement()
    {
        ScriptNode script = Parse("function test() { if(true) { x = 1; } else { x = 2; } }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        var ifStmt = Assert.IsType<IfStmtNode>(fn.Body.Statements.First(n => n is IfStmtNode));
        Assert.NotNull(ifStmt.Else);
    }

    [Fact]
    public void Parse_WhileLoop()
    {
        ScriptNode script = Parse("function test() { while(true) { x = 1; } }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        Assert.Contains(fn.Body.Statements, n => n is WhileStmtNode);
    }

    [Fact]
    public void Parse_ForLoop()
    {
        ScriptNode script = Parse("function test() { for(i = 0; i < 10; i++) { x = 1; } }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        Assert.Contains(fn.Body.Statements, n => n is ForStmtNode);
    }

    [Fact]
    public void Parse_ForeachLoop()
    {
        ScriptNode script = Parse("function test() { foreach(item in array) { x = 1; } }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        Assert.Contains(fn.Body.Statements, n => n is ForeachStmtNode);
    }

    [Fact]
    public void Parse_SwitchStatement()
    {
        ScriptNode script = Parse("function test() { switch(x) { case 1: break; case 2: break; } }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        Assert.Contains(fn.Body.Statements, n => n is SwitchStmtNode);
    }

    [Fact]
    public void Parse_FunctionCall()
    {
        ScriptNode script = Parse("function test() { doThing(1, 2); }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        Assert.NotEmpty(fn.Body.Statements);
        Assert.IsType<ExprStmtNode>(fn.Body.Statements.First!.Value);
    }

    [Fact]
    public void Parse_ReturnStatement()
    {
        ScriptNode script = Parse("function test() { return 42; }");

        var fn = Assert.IsType<FunDefnNode>(Assert.Single(script.ScriptDefns));
        Assert.Contains(fn.Body.Statements, n => n is ReturnStmtNode);
    }

    [Fact]
    public void Parse_NamespaceDirective()
    {
        ScriptNode script = Parse("#namespace test;");

        Assert.Single(script.ScriptDefns);
        Assert.IsType<NamespaceNode>(script.ScriptDefns[0]);
    }

    [Fact]
    public void Parse_MultipleFunctions()
    {
        ScriptNode script = Parse("function a() {} function b() {}");

        Assert.Equal(2, script.ScriptDefns.Count);
        Assert.All(script.ScriptDefns, d => Assert.IsType<FunDefnNode>(d));
    }
}
