
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Data;

public sealed class DocumentTokensLibrary
{
    private List<Token> TokenList { get; set; } = new();

    /// <summary>
    /// Release the backing token list to allow GC. Used for index-mode scripts after analysis.
    /// </summary>
    public void Clear() => TokenList = [];

    private class TokenKeyComparer : IComparer<Token>
    {
        public int Compare(Token? x, Token? y)
        {
            if (x is null)
            {
                return y is null ? 0 : -1;
            }
            if (y is null)
            {
                return 1;
            }

            int lineComparison = x.TokenRange.StartLine.CompareTo(y.TokenRange.StartLine);
            if (lineComparison != 0)
            {
                return lineComparison;
            }
            return x.TokenRange.StartChar.CompareTo(y.TokenRange.StartChar);
        }
    }

    private static readonly TokenKeyComparer _tokenKeyComparer = new();

    private readonly Token _searchToken = new(TokenType.Unknown, TokenRange.Empty);

    /// <summary>
    /// Returns the index of the token at or before the specified position, or -1 if not found.
    /// </summary>
    internal int GetIndex(Position location)
    {
        Token searchToken = _searchToken with { TokenRange = new(location.Line, location.Character, location.Line, location.Character) };

        int index = TokenList.BinarySearch(searchToken, _tokenKeyComparer);

        if (index < 0)
        {
            index = Math.Max(0, ~index - 1);
        }

        if (index >= TokenList.Count)
        {
            return -1;
        }

        return index;
    }

    /// <summary>
    /// Gets the nearest token at or before the specified position.
    /// </summary>
    internal Token? Get(Position location)
    {
        int index = GetIndex(location);
        return index >= 0 ? TokenList[index] : null;
    }

    /// <summary>
    /// Gets a token by its index in the list, or null if out of bounds.
    /// </summary>
    internal Token? GetAt(int index)
    {
        return (uint)index < (uint)TokenList.Count ? TokenList[index] : null;
    }

    /// <summary>
    /// Returns the index of the next non-whitespace/trivia token after fromIndex, or -1 if none.
    /// </summary>
    internal int NextNonTriviaIndex(int fromIndex)
    {
        for (int i = fromIndex + 1; i < TokenList.Count; i++)
        {
            Token t = TokenList[i];
            if (!t.IsWhitespacey() && !t.IsComment())
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the index of the previous non-whitespace/trivia token before fromIndex, or -1 if none.
    /// </summary>
    internal int PrevNonTriviaIndex(int fromIndex)
    {
        for (int i = fromIndex - 1; i >= 0; i--)
        {
            Token t = TokenList[i];
            if (!t.IsWhitespacey() && !t.IsComment())
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the index of the next non-whitespace token after fromIndex, or -1 if none.
    /// Comments are NOT skipped (only whitespace-like tokens).
    /// </summary>
    internal int NextNonWhitespaceIndex(int fromIndex)
    {
        for (int i = fromIndex + 1; i < TokenList.Count; i++)
        {
            if (!TokenList[i].IsWhitespacey())
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns the index of the previous non-whitespace token before fromIndex, or -1 if none.
    /// Comments are NOT skipped (only whitespace-like tokens).
    /// </summary>
    internal int PrevNonWhitespaceIndex(int fromIndex)
    {
        for (int i = fromIndex - 1; i >= 0; i--)
        {
            if (!TokenList[i].IsWhitespacey())
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Finds the index of a specific token by reference equality, using binary search on position
    /// then scanning nearby for the exact reference.
    /// </summary>
    internal int IndexOf(Token token)
    {
        // Binary search to get close to the right position
        int index = TokenList.BinarySearch(token, _tokenKeyComparer);

        if (index < 0)
        {
            index = Math.Max(0, ~index - 1);
        }

        // Scan nearby for exact reference match (tokens at the same position or adjacent)
        int start = Math.Max(0, index - 2);
        int end = Math.Min(TokenList.Count, index + 3);
        for (int i = start; i < end; i++)
        {
            if (ReferenceEquals(TokenList[i], token))
            {
                return i;
            }
        }

        Token token = TokenList[index];

        // If current token is an open paren, use the previous token instead (e.g., function name before the paren)
        if (token.Type == TokenType.OpenParen && index > 0)
        {
            return TokenList[index - 1];
        }

        return token;
    }

    /// <summary>
    /// The number of tokens in the library.
    /// </summary>
    internal int Count => TokenList.Count;

    /// <summary>
    /// Once the token list has finished its final transformations, extract tokens into a flat list.
    /// The LinkedToken chain can then be discarded by the caller.
    /// </summary>
    /// <param name="startNode">The first linked token (SOF) in the chain</param>
    internal void AddRange(LinkedToken startNode)
    {
        // Skip SOF and EOF
        LinkedToken? currentNode = startNode.Next;

        while(currentNode is not null && currentNode.Type != TokenType.Eof)
        {
            TokenList.Add(currentNode.Token);
            currentNode = currentNode.Next;
        }
        TokenList.TrimExcess();
    }

    internal IEnumerable<Token> GetAll()
    {
        return TokenList;
    }
}
