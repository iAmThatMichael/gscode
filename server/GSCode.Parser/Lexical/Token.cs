using GSCode.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Lexical;

/// <summary>
/// Compact value-type range stored inline on Token (8 bytes total).
/// Eliminates 3 heap allocations (Range class + 2 Position classes) per token.
/// Uses ushort fields — max 65535 lines / 65535 columns.
/// </summary>
public readonly record struct TokenRange(ushort StartLine, ushort StartChar, ushort EndLine, ushort EndChar)
{
    public const int MaxLine = ushort.MaxValue;
    public const int MaxChar = ushort.MaxValue;

    public static TokenRange Empty => default;

    public TokenRange(int startLine, int startChar, int endLine, int endChar)
        : this((ushort)startLine, (ushort)startChar, (ushort)endLine, (ushort)endChar) { }

    public Range ToRange() => RangeHelper.From(StartLine, StartChar, EndLine, EndChar);

    public static TokenRange FromRange(Range range) => new(
        range.Start.Line, range.Start.Character,
        range.End.Line, range.End.Character);

    public bool Contains(OmniSharp.Extensions.LanguageServer.Protocol.Models.Position position)
    {
        int line = position.Line, ch = position.Character;
        bool afterStart = line > StartLine || (line == StartLine && ch >= StartChar);
        bool beforeEnd  = line < EndLine   || (line == EndLine   && ch < EndChar);
        return afterStart && beforeEnd;
    }
}

internal record class Token(TokenType Type, TokenRange TokenRange)
{
    /// <summary>
    /// Whether this token originated from a preprocessor expansion (#insert or #define macro).
    /// Tokens marked as from-preprocessor are excluded from semantic tokens and hover definitions
    /// to prevent duplicate highlights on macro-expanded content.
    /// </summary>
    public bool IsFromPreprocessor { get; set; }

    /// <summary>
    /// For tokens that came from an #insert'd file, the resolved absolute path of that file.
    /// Null for tokens that originate in the current script or from #define macro expansion.
    /// Used by the macro attributor in Preprocessor.Defines to key the MacroDefinitionCache
    /// correctly without relying on line-number comparisons across different coordinate spaces.
    /// </summary>
    public string? InsertSourcePath { get; set; }

    /// <summary>
    /// The text content of this token. For tokens with fixed content (keywords, operators, punctuation),
    /// this is resolved from the TokenType without storing a string per-token.
    /// For tokens with dynamic content, this is overridden by LexemeToken.
    /// </summary>
    public virtual string Lexeme => TokenTypeLexemes.Get(Type)!;

    /// <summary>
    /// Creates an LSP <see cref="Range"/> on demand. For hot paths, prefer accessing TokenRange directly.
    /// </summary>
    public Range Range => TokenRange.ToRange();

    public int Length => Lexeme.Length;

    /// <summary>
    /// Link to the previous token in the stored flat list. Set by DocumentTokensLibrary.
    /// </summary>
    public Token? Previous { get; set; }

    /// <summary>
    /// Link to the next token in the stored flat list. Set by DocumentTokensLibrary.
    /// </summary>
    public Token? Next { get; set; }

    public bool IsWhitespacey()
    {
        return Type == TokenType.Whitespace
            || Type == TokenType.LineBreak
            || Type == TokenType.Backslash
            || Type == TokenType.LineComment
            || Type == TokenType.MultilineComment
            || Type == TokenType.DocComment;
    }

    public bool IsComment()
    {
        return Type == TokenType.LineComment
            || Type == TokenType.MultilineComment
            || Type == TokenType.DocComment;
    }

    /// <summary>
    /// Gets the previous non-whitespace token, or null if at start.
    /// </summary>
    public Token? PreviousNonWhitespace()
    {
        Token? current = Previous;
        while (current != null && current.IsWhitespacey())
        {
            current = current.Previous;
        }
        return current;
    }

    /// <summary>
    /// Gets the next non-whitespace token, or null if at end.
    /// </summary>
    public Token? NextNonWhitespace()
    {
        Token? current = Next;
        while (current != null && current.IsWhitespacey())
        {
            current = current.Next;
        }
        return current;
    }

    /// <summary>
    /// Gets the previous non-trivia (non-whitespace and non-comment) token, or null if at start.
    /// </summary>
    public Token? PreviousNonTrivia()
    {
        Token? current = Previous;
        while (current != null && (current.IsWhitespacey() || current.IsComment()))
        {
            current = current.Previous;
        }
        return current;
    }

    /// <summary>
    /// Gets the next non-trivia (non-whitespace and non-comment) token, or null if at end.
    /// </summary>
    public Token? NextNonTrivia()
    {
        Token? current = Next;
        while (current != null && (current.IsWhitespacey() || current.IsComment()))
        {
            current = current.Next;
        }
        return current;
    }

    public bool IsKeyword()
    {
        return Type == TokenType.Classes ||
               Type == TokenType.Function ||
               Type == TokenType.Var ||
               Type == TokenType.Return ||
               Type == TokenType.Thread ||
               Type == TokenType.Class ||
               Type == TokenType.Anim ||
               Type == TokenType.If ||
               Type == TokenType.Else ||
               Type == TokenType.Do ||
               Type == TokenType.While ||
               Type == TokenType.Foreach ||
               Type == TokenType.For ||
               Type == TokenType.In ||
               Type == TokenType.New ||
               Type == TokenType.Switch ||
               Type == TokenType.Case ||
               Type == TokenType.Default ||
               Type == TokenType.Break ||
               Type == TokenType.Continue ||
               Type == TokenType.Constructor ||
               Type == TokenType.Destructor ||
               Type == TokenType.Autoexec ||
               Type == TokenType.Private ||
               Type == TokenType.Const;
    }
}

/// <summary>
/// Token subtype for tokens that carry a dynamic lexeme (identifiers, strings, comments, numbers, etc.).
/// Tokens with fixed lexemes (keywords, operators, punctuation) use the base Token class instead,
/// saving the 8-byte string reference per token.
/// </summary>
internal sealed record class LexemeToken(TokenType Type, TokenRange TokenRange, string RawLexeme)
    : Token(Type, TokenRange)
{
    public override string Lexeme => RawLexeme;
}

/// <summary>
/// Parse-time wrapper that adds linked-list navigation (Next/Previous) around a Token.
/// Discarded after analysis — the Token objects are stored in DocumentTokensLibrary
/// without these pointers, saving 16 bytes per token in steady state.
/// </summary>
internal class LinkedToken(Token token)
{
    public readonly Token Token = token;
    public LinkedToken? Next;
    public LinkedToken? Previous;

    // Convenience delegates so callers rarely need .Token
    public TokenType Type => Token.Type;
    public TokenRange TokenRange => Token.TokenRange;
    public Range Range => Token.Range;
    public string Lexeme => Token.Lexeme;
    public bool IsWhitespacey() => Token.IsWhitespacey();
    public bool IsComment() => Token.IsComment();
    public bool IsKeyword() => Token.IsKeyword();

    /// <summary>
    /// Creates a new LinkedToken wrapping a clone of the inner Token with the specified range override.
    /// </summary>
    public LinkedToken CloneWith(TokenRange? rangeOverride = null, bool? isFromPreprocessor = null)
    {
        Token cloned = Token with
        {
            TokenRange = rangeOverride ?? Token.TokenRange,
            IsFromPreprocessor = isFromPreprocessor ?? Token.IsFromPreprocessor
        };
        return new LinkedToken(cloned);
    }
}

/// <summary>
/// Container to represent a sequence of linked tokens used during lex/preprocess/parse.
/// </summary>
/// <param name="Start">The beginning linked token of the sequence.</param>
/// <param name="End">The ending linked token of the sequence.</param>
internal record struct TokenList(LinkedToken? Start, LinkedToken? End)
{
    public readonly Range? Range { get; } = Start != null && End != null
        ? RangeHelper.From(Start.TokenRange.StartLine, Start.TokenRange.StartChar, End.TokenRange.EndLine, End.TokenRange.EndChar)
        : default;

    public static TokenList Empty => new(null, null);

    public readonly bool IsEmpty => Start == null || End == null;

    public static TokenList From(params Token[] tokens)
    {
        if (tokens.Length == 0)
        {
            return Empty;
        }

        LinkedToken[] nodes = new LinkedToken[tokens.Length];

        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = new LinkedToken(tokens[i] with { TokenRange = TokenRange.Empty });

            nodes[i].Previous = i > 0 ? nodes[i - 1] : null;
            if (i - 1 >= 0)
            {
                nodes[i - 1].Next = nodes[i];
            }
        }

        return new TokenList(nodes[0], nodes[^1]);
    }

    public readonly void ConnectToTokens(LinkedToken before, LinkedToken after)
    {
        // Nothing to connect
        if (Start == null || End == null)
        {
            before.Next = after;
            after.Previous = before;
            return;
        }

        // Otherwise connect our list
        before.Next = Start;
        Start.Previous = before;

        End.Next = after;
        after.Previous = End;
    }

    public TokenList CloneList(TokenRange? withRange = null, bool markAsPreprocessor = false)
    {
        if (Start == null || End == null)
        {
            return Empty;
        }

        bool? preprocessorFlag = markAsPreprocessor ? true : null;

        LinkedToken currentNode = Start;
        // Populate the first node.
        LinkedToken firstNode = currentNode.CloneWith(withRange, preprocessorFlag);
        LinkedToken lastNode = firstNode;

        while (currentNode != End)
        {
            currentNode = currentNode.Next!;

            // Clone the current token with the updated range
            LinkedToken clonedNode = currentNode.CloneWith(withRange, preprocessorFlag);

            // Connect the cloned node to the previous one in the output chain
            lastNode.Next = clonedNode;
            clonedNode.Previous = lastNode;

            // Update the previous reference and move to the next
            lastNode = clonedNode;
        }

        return new TokenList(firstNode, lastNode);
    }

    /// <summary>
    /// Produces a clean string of the snippet of raw code this token list represents.
    /// </summary>
    public readonly string ToSnippetString()
    {
        if (Start == null || End == null)
        {
            return string.Empty;
        }

        StringBuilder sb = new();

        LinkedToken current = FirstNonWhitespaceNode()!;
        LinkedToken last = LastNonWhitespaceNode()!;
        bool lastAddedWhitespace = false;

        do
        {
            // Only ever add one whitespace token in a chain of them, so we don't get snippets with multiple spaces
            if (!lastAddedWhitespace || !current.IsWhitespacey())
            {
                lastAddedWhitespace = current.IsWhitespacey();
                // If we've reached whitespace, just emit a single space and don't do this repeatedly
                sb.Append(lastAddedWhitespace ? ' ' : current.Lexeme);
            }

            // Go to next
            if (current == last)
            {
                break;
            }
            current = current.Next!;
        } while (current is not null && current.Type != TokenType.Eof);

        if (current!.Type == TokenType.Eof)
        {
            Console.Error.WriteLine("sanity check failed");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Produces a source-exact string of the snippet of raw code this token list represents.
    /// </summary>
    public readonly string ToRawString()
    {
        if (Start == null || End == null)
        {
            return string.Empty;
        }

        StringBuilder sb = new();

        LinkedToken current = Start;
        LinkedToken last = End;

        do
        {
            sb.Append(current.Lexeme);

            // Go to next
            if (current == last)
            {
                break;
            }
            current = current.Next!;
        } while (current is not null);

        return sb.ToString();
    }

    public readonly LinkedToken? FirstNonWhitespaceNode()
    {
        if (Start == null || End == null)
        {
            return null;
        }

        LinkedToken current = Start;
        while (current.Type == TokenType.Whitespace && current != End)
        {
            current = current.Next!;
        }

        return current;
    }

    public readonly LinkedToken? LastNonWhitespaceNode()
    {
        if (Start == null || End == null)
        {
            return null;
        }

        LinkedToken current = End;
        while (current.Type == TokenType.Whitespace && current != Start)
        {
            current = current.Previous!;
        }

        return current;
    }
}

/// <summary>
/// Provides O(1) lookup of fixed lexeme strings for token types whose text content
/// is fully determined by the type (keywords, operators, punctuation).
/// Returns null for types that require a real lexeme (identifiers, literals, comments, etc.).
/// </summary>
internal static class TokenTypeLexemes
{
    private static readonly string?[] _lookupTable;

    static TokenTypeLexemes()
    {
        byte maxValue = Enum.GetValues<TokenType>().Cast<byte>().Max();
        _lookupTable = new string?[maxValue + 1];

        // Punctuation
        _lookupTable[(byte)TokenType.OpenParen] = "(";
        _lookupTable[(byte)TokenType.CloseParen] = ")";
        _lookupTable[(byte)TokenType.OpenBracket] = "[";
        _lookupTable[(byte)TokenType.CloseBracket] = "]";
        _lookupTable[(byte)TokenType.OpenBrace] = "{";
        _lookupTable[(byte)TokenType.CloseBrace] = "}";
        _lookupTable[(byte)TokenType.OpenDevBlock] = "/#";
        _lookupTable[(byte)TokenType.CloseDevBlock] = "#/";

        // Operators
        _lookupTable[(byte)TokenType.IdentityNotEquals] = "!==";
        _lookupTable[(byte)TokenType.IdentityEquals] = "===";
        _lookupTable[(byte)TokenType.ScopeResolution] = "::";
        _lookupTable[(byte)TokenType.Dot] = ".";
        _lookupTable[(byte)TokenType.Arrow] = "->";
        _lookupTable[(byte)TokenType.And] = "&&";
        _lookupTable[(byte)TokenType.BitLeftShiftAssign] = "<<=";
        _lookupTable[(byte)TokenType.BitRightShiftAssign] = ">>=";
        _lookupTable[(byte)TokenType.BitAndAssign] = "&=";
        _lookupTable[(byte)TokenType.BitOrAssign] = "|=";
        _lookupTable[(byte)TokenType.BitXorAssign] = "^=";
        _lookupTable[(byte)TokenType.DivideAssign] = "/=";
        _lookupTable[(byte)TokenType.MinusAssign] = "-=";
        _lookupTable[(byte)TokenType.ModuloAssign] = "%=";
        _lookupTable[(byte)TokenType.MultiplyAssign] = "*=";
        _lookupTable[(byte)TokenType.PlusAssign] = "+=";
        _lookupTable[(byte)TokenType.BitLeftShift] = "<<";
        _lookupTable[(byte)TokenType.BitRightShift] = ">>";
        _lookupTable[(byte)TokenType.Decrement] = "--";
        _lookupTable[(byte)TokenType.Equals] = "==";
        _lookupTable[(byte)TokenType.GreaterThanEquals] = ">=";
        _lookupTable[(byte)TokenType.Increment] = "++";
        _lookupTable[(byte)TokenType.LessThanEquals] = "<=";
        _lookupTable[(byte)TokenType.NotEquals] = "!=";
        _lookupTable[(byte)TokenType.Or] = "||";
        _lookupTable[(byte)TokenType.Assign] = "=";
        _lookupTable[(byte)TokenType.BitAnd] = "&";
        _lookupTable[(byte)TokenType.BitNot] = "~";
        _lookupTable[(byte)TokenType.BitOr] = "|";
        _lookupTable[(byte)TokenType.BitXor] = "^";
        _lookupTable[(byte)TokenType.Divide] = "/";
        _lookupTable[(byte)TokenType.GreaterThan] = ">";
        _lookupTable[(byte)TokenType.LessThan] = "<";
        _lookupTable[(byte)TokenType.Minus] = "-";
        _lookupTable[(byte)TokenType.Modulo] = "%";
        _lookupTable[(byte)TokenType.Multiply] = "*";
        _lookupTable[(byte)TokenType.Not] = "!";
        _lookupTable[(byte)TokenType.Plus] = "+";
        _lookupTable[(byte)TokenType.QuestionMark] = "?";
        _lookupTable[(byte)TokenType.Colon] = ":";

        // Special tokens
        _lookupTable[(byte)TokenType.Semicolon] = ";";
        _lookupTable[(byte)TokenType.Comma] = ",";
        _lookupTable[(byte)TokenType.VarargDots] = "...";
        _lookupTable[(byte)TokenType.Backslash] = "\\";
        _lookupTable[(byte)TokenType.Hash] = "#";

        // Keywords
        _lookupTable[(byte)TokenType.Classes] = "classes";
        _lookupTable[(byte)TokenType.Function] = "function";
        _lookupTable[(byte)TokenType.Var] = "var";
        _lookupTable[(byte)TokenType.Return] = "return";
        _lookupTable[(byte)TokenType.Thread] = "thread";
        _lookupTable[(byte)TokenType.Class] = "class";
        _lookupTable[(byte)TokenType.Anim] = "anim";
        _lookupTable[(byte)TokenType.If] = "if";
        _lookupTable[(byte)TokenType.Else] = "else";
        _lookupTable[(byte)TokenType.Do] = "do";
        _lookupTable[(byte)TokenType.While] = "while";
        _lookupTable[(byte)TokenType.Foreach] = "foreach";
        _lookupTable[(byte)TokenType.For] = "for";
        _lookupTable[(byte)TokenType.In] = "in";
        _lookupTable[(byte)TokenType.New] = "new";
        _lookupTable[(byte)TokenType.Switch] = "switch";
        _lookupTable[(byte)TokenType.Case] = "case";
        _lookupTable[(byte)TokenType.Default] = "default";
        _lookupTable[(byte)TokenType.Break] = "break";
        _lookupTable[(byte)TokenType.Continue] = "continue";
        _lookupTable[(byte)TokenType.Constructor] = "constructor";
        _lookupTable[(byte)TokenType.Destructor] = "destructor";
        _lookupTable[(byte)TokenType.Autoexec] = "autoexec";
        _lookupTable[(byte)TokenType.Private] = "private";
        _lookupTable[(byte)TokenType.Const] = "const";

        // Preprocessor keywords
        _lookupTable[(byte)TokenType.UsingAnimTree] = "#using_animtree";
        _lookupTable[(byte)TokenType.Using] = "#using";
        _lookupTable[(byte)TokenType.Insert] = "#insert";
        _lookupTable[(byte)TokenType.Define] = "#define";
        _lookupTable[(byte)TokenType.Namespace] = "#namespace";
        _lookupTable[(byte)TokenType.Precache] = "#precache";
        _lookupTable[(byte)TokenType.PreIf] = "#if";
        _lookupTable[(byte)TokenType.PreElIf] = "#elif";
        _lookupTable[(byte)TokenType.PreElse] = "#else";
        _lookupTable[(byte)TokenType.PreEndIf] = "#endif";

        // Reserved functions
        _lookupTable[(byte)TokenType.WaittillFrameEnd] = "waittillframeend";
        _lookupTable[(byte)TokenType.WaittillMatch] = "waittillmatch";
        _lookupTable[(byte)TokenType.Waittill] = "waittill";
        _lookupTable[(byte)TokenType.WaitRealTime] = "waitrealtime";
        _lookupTable[(byte)TokenType.Wait] = "wait";

        // Fixed literals
        _lookupTable[(byte)TokenType.Undefined] = "undefined";
        _lookupTable[(byte)TokenType.False] = "false";
        _lookupTable[(byte)TokenType.True] = "true";

        // Whitespace
        _lookupTable[(byte)TokenType.LineBreak] = "<EOL>";
    }

    /// <summary>
    /// Returns the fixed lexeme for a token type, or null if the type requires a real lexeme.
    /// </summary>
    public static string? Get(TokenType type)
    {
        byte index = (byte)type;
        return index < _lookupTable.Length ? _lookupTable[index] : null;
    }

    /// <summary>
    /// Returns true if this token type has a fixed lexeme (i.e. doesn't need a stored string).
    /// </summary>
    public static bool HasFixedLexeme(TokenType type) => Get(type) is not null;
}

internal enum TokenType : byte
{
    // Misc
    Sof,
    Eof,
    Whitespace,
    LineBreak,
    Unknown,

    // Error types
    ErrorString,

    // Comments
    LineComment,
    MultilineComment,
    DocComment,

    // Punctuation
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    OpenBrace,
    CloseBrace,
    OpenDevBlock,
    CloseDevBlock,

    // Operators
    IdentityNotEquals, // !==
    IdentityEquals, // ===
    ScopeResolution, // ::
    Dot, // .
    Arrow, // ->
    And, // &&
    BitLeftShiftAssign, // <<=
    BitRightShiftAssign, // >>=
    BitAndAssign, // &=
    BitOrAssign, // |=
    BitXorAssign, // ^=
    DivideAssign, // /=
    MinusAssign, // -=
    ModuloAssign, // %=
    MultiplyAssign, // *=
    PlusAssign, // +=
    BitLeftShift, // <<
    BitRightShift, // >>
    Decrement, // --
    Equals, // ==
    GreaterThanEquals, // >=
    Increment, // ++
    LessThanEquals, // <=
    NotEquals, // !=
    Or, // ||
    Assign, // =
    BitAnd, // &
    BitNot, // ~
    BitOr, // |
    BitXor, // ^
    Divide, // /
    GreaterThan, // >
    LessThan, // <
    Minus, // -
    Modulo, // %
    Multiply, // *
    Not, // !
    Plus, // +
    QuestionMark, // ?
    Colon, // :

    // Special Tokens
    Semicolon, // ;
    Comma, // ,
    VarargDots, // ...
    Backslash, // \
    Hash, // #

    // Keywords
    Classes, // classes
    Function, // function
    Var, // var
    Return, // return
    Thread, // thread
    Class, // class
    Anim, // anim
    If, // if
    Else, // else
    Do, // do
    While, // while
    Foreach, // foreach
    For, // for
    In, // in
    New, // new
    Switch, // switch
    Case, // case
    Default, // default
    Break, // break
    Continue, // continue
    Constructor, // constructor
    Destructor, // destructor
    Autoexec, // autoexec
    Private, // private
    Const, // const

    // Preprocessor keywords
    UsingAnimTree, // #using_animtree
    Using, // #using
    Insert, // #insert
    Define, // #define
    Namespace, // #namespace
    Precache, // #precache
    PreIf, // #if
    PreElIf, // #elif
    PreElse, // #else
    PreEndIf, // #endif


    // Reserved functions (case-insensitive) TODO
    WaittillFrameEnd, // waittillframeend
    WaittillMatch, // waittillmatch
    Waittill, // waittill
    WaitRealTime, // waitrealtime
    Wait, // wait
    //AssertMsg, // assertmsg
    //Assert, // assert
    //VectorScale, // vectorscale
    //GetTime, // getttime
    //ProfileStart, // profilestart
    //ProfileStop, // profilestop

    // Literals
    Undefined, // undefined
    False, // false
    True, // true
    String, // "string"
    // ReSharper disable once InconsistentNaming
    IString, // &"string"
    CompilerHash, // #"string"
    Integer, // 123
    Float, // 123.456
    Hex, // 0x123
    AnimTree, // #animtree

    // Identifier
    Identifier, // name
    AnimIdentifier, // %name
}