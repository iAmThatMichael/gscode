using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
namespace GSCode.Tests;

internal static class TestHelper
{
    /// <summary>
    /// Lexes source code and returns the raw TokenList (linked token chain).
    /// </summary>
    public static TokenList Lex(string source)
    {
        Lexer lexer = new(source.AsSpan());
        return lexer.Transform();
    }

    /// <summary>
    /// Lexes source code and returns a flat list of tokens, filtering out
    /// Sof, Eof, Whitespace, and LineBreak tokens by default.
    /// </summary>
    public static List<Token> LexToList(string source, bool filterTrivia = true)
    {
        TokenList tokenList = Lex(source);
        List<Token> tokens = new();

        LinkedToken? current = tokenList.Start;
        HashSet<LinkedToken> seen = new(ReferenceEqualityComparer.Instance);
        while (current is not null && seen.Add(current))
        {
            if (!filterTrivia || !IsTrivia(current.Token))
            {
                tokens.Add(current.Token);
            }
            current = current.Next;
        }

        return tokens;
    }

    /// <summary>
    /// Creates a dummy ParserIntelliSense for testing.
    /// </summary>
    public static ParserIntelliSense CreateDummySense(string langId = "gsc")
    {
        return new ParserIntelliSense(0, new Uri("file:///test"), langId);
    }

    private static bool IsTrivia(Token token)
    {
        return token.Type is TokenType.Sof or TokenType.Eof
            or TokenType.Whitespace or TokenType.LineBreak;
    }
}

