using GSCode.Parser.Lexical;
using Xunit;

namespace GSCode.Tests;

public class LexerTests
{
    // === Identifiers ===

    [Fact]
    public void Lex_Identifier()
    {
        var tokens = TestHelper.LexToList("myVar");
        var token = Assert.Single(tokens);
        Assert.Equal(TokenType.Identifier, token.Type);
        Assert.Equal("myVar", token.Lexeme);
    }

    // === Numeric literals ===

    [Fact]
    public void Lex_IntegerLiteral()
    {
        var tokens = TestHelper.LexToList("123");
        var token = Assert.Single(tokens);
        Assert.Equal(TokenType.Integer, token.Type);
        Assert.Equal("123", token.Lexeme);
    }

    [Fact]
    public void Lex_FloatLiteral()
    {
        var tokens = TestHelper.LexToList("3.14");
        var token = Assert.Single(tokens);
        Assert.Equal(TokenType.Float, token.Type);
    }

    [Fact]
    public void Lex_HexLiteral()
    {
        var tokens = TestHelper.LexToList("0xFF");
        var token = Assert.Single(tokens);
        Assert.Equal(TokenType.Hex, token.Type);
    }

    // === String literal ===

    [Fact]
    public void Lex_StringLiteral()
    {
        var tokens = TestHelper.LexToList("\"hello world\"");
        var token = Assert.Single(tokens);
        Assert.Equal(TokenType.String, token.Type);
    }

    // === Keywords ===

    [Fact] public void Lex_Keyword_Function() => AssertSingleToken("function", TokenType.Function);
    [Fact] public void Lex_Keyword_If() => AssertSingleToken("if", TokenType.If);
    [Fact] public void Lex_Keyword_Else() => AssertSingleToken("else", TokenType.Else);
    [Fact] public void Lex_Keyword_While() => AssertSingleToken("while", TokenType.While);
    [Fact] public void Lex_Keyword_For() => AssertSingleToken("for", TokenType.For);
    [Fact] public void Lex_Keyword_Foreach() => AssertSingleToken("foreach", TokenType.Foreach);
    [Fact] public void Lex_Keyword_Return() => AssertSingleToken("return", TokenType.Return);
    [Fact] public void Lex_Keyword_Switch() => AssertSingleToken("switch", TokenType.Switch);
    [Fact] public void Lex_Keyword_Case() => AssertSingleToken("case", TokenType.Case);
    [Fact] public void Lex_Keyword_Break() => AssertSingleToken("break", TokenType.Break);
    [Fact] public void Lex_Keyword_Continue() => AssertSingleToken("continue", TokenType.Continue);
    [Fact] public void Lex_Keyword_Class() => AssertSingleToken("class", TokenType.Class);
    [Fact] public void Lex_Keyword_Var() => AssertSingleToken("var", TokenType.Var);
    [Fact] public void Lex_Keyword_Const() => AssertSingleToken("const", TokenType.Const);

    // === Operators ===

    [Fact] public void Lex_Op_Plus() => AssertSingleToken("+", TokenType.Plus);
    [Fact] public void Lex_Op_Minus() => AssertSingleToken("-", TokenType.Minus);
    [Fact] public void Lex_Op_Multiply() => AssertSingleToken("*", TokenType.Multiply);
    [Fact] public void Lex_Op_Divide() => AssertSingleToken("/", TokenType.Divide);
    [Fact] public void Lex_Op_Equals() => AssertSingleToken("==", TokenType.Equals);
    [Fact] public void Lex_Op_NotEquals() => AssertSingleToken("!=", TokenType.NotEquals);
    [Fact] public void Lex_Op_And() => AssertSingleToken("&&", TokenType.And);
    [Fact] public void Lex_Op_Or() => AssertSingleToken("||", TokenType.Or);
    [Fact] public void Lex_Op_ScopeResolution() => AssertSingleToken("::", TokenType.ScopeResolution);
    [Fact] public void Lex_Op_Assign() => AssertSingleToken("=", TokenType.Assign);

    // === Delimiters ===

    [Fact] public void Lex_Delim_OpenParen() => AssertSingleToken("(", TokenType.OpenParen);
    [Fact] public void Lex_Delim_CloseParen() => AssertSingleToken(")", TokenType.CloseParen);
    [Fact] public void Lex_Delim_OpenBrace() => AssertSingleToken("{", TokenType.OpenBrace);
    [Fact] public void Lex_Delim_CloseBrace() => AssertSingleToken("}", TokenType.CloseBrace);
    [Fact] public void Lex_Delim_OpenBracket() => AssertSingleToken("[", TokenType.OpenBracket);
    [Fact] public void Lex_Delim_CloseBracket() => AssertSingleToken("]", TokenType.CloseBracket);
    [Fact] public void Lex_Delim_Semicolon() => AssertSingleToken(";", TokenType.Semicolon);
    [Fact] public void Lex_Delim_Comma() => AssertSingleToken(",", TokenType.Comma);

    // === Multi-token expressions ===

    [Fact]
    public void Lex_MemberAccess()
    {
        var tokens = TestHelper.LexToList("self.field");
        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("self", tokens[0].Lexeme);
        Assert.Equal(TokenType.Dot, tokens[1].Type);
        Assert.Equal(TokenType.Identifier, tokens[2].Type);
        Assert.Equal("field", tokens[2].Lexeme);
    }

    // === Comments ===

    [Fact]
    public void Lex_LineComment()
    {
        var tokens = TestHelper.LexToList("// line comment", filterTrivia: false);
        Assert.Contains(tokens, t => t.Type == TokenType.LineComment);
    }

    [Fact]
    public void Lex_BlockComment()
    {
        var tokens = TestHelper.LexToList("/* block */", filterTrivia: false);
        Assert.Contains(tokens, t => t.Type == TokenType.MultilineComment);
    }

    // === Preprocessor directives ===

    [Fact] public void Lex_Directive_Define() => AssertSingleToken("#define", TokenType.Define);
    [Fact] public void Lex_Directive_Using() => AssertSingleToken("#using", TokenType.Using);
    [Fact] public void Lex_Directive_Namespace() => AssertSingleToken("#namespace", TokenType.Namespace);

    // === Helper ===

    private static void AssertSingleToken(string source, TokenType expected)
    {
        var tokens = TestHelper.LexToList(source);
        var token = Assert.Single(tokens);
        Assert.Equal(expected, token.Type);
    }
}
