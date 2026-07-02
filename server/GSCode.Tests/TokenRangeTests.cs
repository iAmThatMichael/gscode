using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Tests;

/// <summary>
/// Regression test: TokenRange.EndChar is computed as one-past-the-last-character
/// everywhere in the codebase (half-open range), so Contains must treat EndChar as
/// exclusive, matching DocumentTokensLibrary.GetIndex's convention.
/// </summary>
public class TokenRangeTests
{
    [Fact]
    public void Contains_TreatsEndCharAsExclusive()
    {
        // Represents a token/call range spanning columns 4..8 (e.g. "FOO(" through the
        // closing ")"), where EndChar (8) is one past the last real character (7).
        var range = new TokenRange(0, 4, 0, 8);

        Assert.True(range.Contains(new Position(0, 4)));  // at the start — inside
        Assert.True(range.Contains(new Position(0, 7)));  // last real character — inside

        // BUG (pre-fix): Contains returns true here because EndChar is treated as
        // inclusive, misclassifying the cursor position immediately after the range as
        // still "inside" it.
        Assert.False(range.Contains(new Position(0, 8)));
    }
}
