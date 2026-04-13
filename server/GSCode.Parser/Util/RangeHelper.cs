using GSCode.Data;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

// Namespace matches the original GSCode.Data location so all call-sites
// (which use 'using GSCode.Data;') continue to resolve without changes.
namespace GSCode.Data;

/// <summary>
/// Factory helpers for constructing OmniSharp <see cref="Range"/> and
/// <see cref="Position"/> objects, and for converting between those types
/// and the protocol-agnostic <see cref="GsRange"/>/<see cref="GsPosition"/>.
/// Lives in GSCode.Parser (which already depends on OmniSharp) rather than
/// GSCode.Data, keeping the data layer free of protocol dependencies.
/// </summary>
public static class RangeHelper
{
    public static Range From(int startLine, int startCharacter, int endLine, int endCharacter)
        => new()
        {
            Start = new Position(startLine, startCharacter),
            End   = new Position(endLine,   endCharacter)
        };

    public static Range From(Position start, Position end)
        => new() { Start = start, End = end };

    public static Range Empty => From(0, 0, 0, 0);

    // Protocol conversion helpers

    /// <summary>Converts a protocol-agnostic <see cref="GsRange"/> to an OmniSharp <see cref="Range"/>.</summary>
    public static Range ToLspRange(this GsRange r)
        => From(r.Start.Line, r.Start.Character, r.End.Line, r.End.Character);

    /// <summary>Converts an OmniSharp <see cref="Range"/> to a protocol-agnostic <see cref="GsRange"/>.</summary>
    public static GsRange ToGsRange(this Range r)
        => GsRange.From(r.Start.Line, r.Start.Character, r.End.Line, r.End.Character);
}