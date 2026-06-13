namespace GSCode.Data;

/// <summary>
/// A protocol-agnostic range within a text document, defined by start and end positions.
/// Used as the native range type in the data layer, independent of any LSP protocol version.
/// </summary>
public readonly record struct GsRange(GsPosition Start, GsPosition End)
{
    /// <summary>The empty range at (0, 0) to (0, 0).</summary>
    public static GsRange Empty { get; } = default;

    /// <summary>Creates a range from individual line/character coordinates.</summary>
    public static GsRange From(int startLine, int startChar, int endLine, int endChar)
        => new(new GsPosition(startLine, startChar), new GsPosition(endLine, endChar));
}