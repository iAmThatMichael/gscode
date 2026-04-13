namespace GSCode.Data;

/// <summary>
/// A protocol-agnostic 0-based line/character position within a text document.
/// Used as the native position type in the data layer, independent of any LSP protocol version.
/// </summary>
public readonly record struct GsPosition(int Line, int Character);