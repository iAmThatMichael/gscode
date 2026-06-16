namespace GSCode.Parser.Data;

/// <summary>
/// Public façade for managing the static insert-token cache held by <see cref="ParserIntelliSense"/>.
/// This is the only entry point the LSP layer needs; the cache itself stays internal.
/// </summary>
public static class InsertTokenCache
{
    /// <summary>
    /// Evicts a single entry from the static insert-token cache so the next parse of any
    /// script that #insert's <paramref name="resolvedPath"/> re-reads the file from disk.
    /// </summary>
    public static void Invalidate(string resolvedPath) =>
        ParserIntelliSense.InvalidateInsertFile(resolvedPath);
}
