namespace GSCode.Parser.Data;

/// <summary>
/// Provides cross-file field name lookups for global objects (level, world, game, etc.).
/// Implemented by the workspace-level registry in the host project (GSCode.NET),
/// consumed by <see cref="DocumentCompletionsLibrary"/> for dot-access completions.
/// </summary>
/// <remarks>
/// This interface breaks the circular dependency between GSCode.Parser and GSCode.NET,
/// following the same pattern as <see cref="SA.ISymbolLocationProvider"/>.
/// </remarks>
public interface IGlobalFieldProvider
{
    /// <summary>
    /// Returns all known field names for the given global object identifier (e.g., "level", "world", "game").
    /// Field names are returned in lowercase.
    /// </summary>
    /// <param name="ownerIdentifier">The identifier of the global object (case-insensitive).</param>
    /// <returns>A collection of field names, or empty if the identifier is not tracked.</returns>
    IReadOnlyCollection<string> GetFieldNames(string ownerIdentifier);

    /// <summary>
    /// Returns whether the given identifier is a tracked global object (e.g., "level", "world", "game").
    /// </summary>
    bool IsTrackedOwner(string identifier);
}
