using System.Collections.Concurrent;
using GSCode.Parser.Data;

namespace GSCode.NET.LSP;

/// <summary>
/// Identifies which global object a field belongs to.
/// Extensible — add new owners here as tracking is implemented for them.
/// </summary>
public enum FieldOwner
{
    /// <summary>Global level struct — fields survive for the duration of a level.</summary>
    Level,
    /// <summary>Global world struct — fields persist across levels for the game session.</summary>
    World,
    /// <summary>Global game array — fields persist for the duration of a match.</summary>
    Game,
    // Future: Self, Class instances, etc.
}

/// <summary>
/// Describes a field observed on a global object, including its source location.
/// </summary>
/// <param name="Owner">Which global object this field was observed on.</param>
/// <param name="FieldName">The field name (case-insensitive in GSC).</param>
/// <param name="FilePath">The file where this field was observed.</param>
public sealed record GlobalFieldEntry(FieldOwner Owner, string FieldName, string FilePath);

/// <summary>
/// A workspace-wide registry that tracks field names observed on global objects
/// (<c>level</c>, <c>world</c>, <c>game</c>) across all parsed scripts.
/// Thread-safe and designed for concurrent access during workspace indexing.
/// </summary>
/// <remarks>
/// Follows the same lifecycle pattern as <see cref="GlobalSymbolRegistry"/>:
/// <list type="bullet">
///   <item>After parsing a file, call <see cref="UpdateFieldsForFile"/> with the extracted fields.</item>
///   <item>When a file is closed/removed, call <see cref="RemoveFieldsFromFile"/>.</item>
///   <item>At completion time, call <see cref="GetFieldNames"/> to get all known field names for an owner.</item>
/// </list>
/// </remarks>
public sealed class GlobalFieldRegistry : IGlobalFieldProvider
{
    /// <summary>
    /// Primary index: owner → fieldName (lowered) → set of file paths that reference this field.
    /// The inner ConcurrentDictionary&lt;string, byte&gt; is used as a concurrent hash-set.
    /// </summary>
    private readonly ConcurrentDictionary<FieldOwner, ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>> _fields = new();

    /// <summary>
    /// Reverse index: filePath → set of (owner, fieldName) pairs defined in that file.
    /// Enables efficient cleanup when a file is removed or reparsed.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(FieldOwner Owner, string FieldName), byte>> _fileIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Updates the field set for a specific file. Adds new fields and removes stale ones.
    /// Call after every parse of a script file.
    /// </summary>
    /// <param name="filePath">Absolute path of the parsed file.</param>
    /// <param name="fields">All (owner, fieldName) pairs observed in this file.</param>
    public void UpdateFieldsForFile(string filePath, IEnumerable<(FieldOwner Owner, string FieldName)> fields)
    {
        var newEntries = new HashSet<(FieldOwner Owner, string FieldName)>();

        foreach (var (owner, fieldName) in fields)
        {
            string normalizedName = fieldName.ToLowerInvariant();
            var key = (owner, normalizedName);
            newEntries.Add(key);

            // Add to primary index
            var ownerDict = _fields.GetOrAdd(owner, _ => new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase));
            var filePaths = ownerDict.GetOrAdd(normalizedName, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            filePaths[filePath] = 0;
        }

        // Get existing entries for this file
        var existingEntries = _fileIndex.GetOrAdd(filePath, _ => new ConcurrentDictionary<(FieldOwner, string), byte>());

        // Remove entries that are no longer present in this file
        foreach (var oldKey in existingEntries.Keys)
        {
            if (!newEntries.Contains(oldKey))
            {
                RemoveFieldEntry(filePath, oldKey.Owner, oldKey.FieldName);
                existingEntries.TryRemove(oldKey, out _);
            }
        }

        // Update file index with current entries
        foreach (var key in newEntries)
        {
            existingEntries[key] = 0;
        }
    }

    /// <summary>
    /// Removes all field entries associated with a file.
    /// Call when a file is closed or removed from the workspace.
    /// </summary>
    public void RemoveFieldsFromFile(string filePath)
    {
        if (!_fileIndex.TryRemove(filePath, out var entries))
            return;

        foreach (var (owner, fieldName) in entries.Keys)
        {
            RemoveFieldEntry(filePath, owner, fieldName);
        }
    }

    /// <summary>
    /// Returns all known field names for the given owner across the entire workspace.
    /// </summary>
    public IReadOnlyCollection<string> GetFieldNames(FieldOwner owner)
    {
        if (_fields.TryGetValue(owner, out var ownerDict))
        {
            return ownerDict.Keys.ToList();
        }
        return [];
    }

    /// <summary>
    /// Returns all known field names for the given owner, with the set of files each was observed in.
    /// Useful for showing provenance in completion details.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> GetFieldsWithSources(FieldOwner owner)
    {
        var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);

        if (_fields.TryGetValue(owner, out var ownerDict))
        {
            foreach (var (fieldName, filePaths) in ownerDict)
            {
                result[fieldName] = filePaths.Keys.ToList();
            }
        }

        return result;
    }

    /// <summary>
    /// Maps a GSC identifier token to the corresponding <see cref="FieldOwner"/>, if it is a tracked global.
    /// Returns <c>null</c> for identifiers that are not tracked (e.g., <c>self</c>, local structs).
    /// </summary>
    public static FieldOwner? IdentifierToOwner(string identifier)
    {
        if (identifier.Equals("level", StringComparison.OrdinalIgnoreCase)) return FieldOwner.Level;
        if (identifier.Equals("world", StringComparison.OrdinalIgnoreCase)) return FieldOwner.World;
        if (identifier.Equals("game", StringComparison.OrdinalIgnoreCase)) return FieldOwner.Game;
        return null;
    }

    private void RemoveFieldEntry(string filePath, FieldOwner owner, string normalizedFieldName)
    {
        if (!_fields.TryGetValue(owner, out var ownerDict))
            return;

        if (!ownerDict.TryGetValue(normalizedFieldName, out var filePaths))
            return;

        filePaths.TryRemove(filePath, out _);

        // Clean up empty entries
        if (filePaths.IsEmpty)
        {
            ownerDict.TryRemove(normalizedFieldName, out _);
        }
    }

    #region IGlobalFieldProvider

    /// <inheritdoc/>
    IReadOnlyCollection<string> IGlobalFieldProvider.GetFieldNames(string ownerIdentifier)
    {
        var owner = IdentifierToOwner(ownerIdentifier);
        return owner.HasValue ? GetFieldNames(owner.Value) : [];
    }

    /// <inheritdoc/>
    bool IGlobalFieldProvider.IsTrackedOwner(string identifier) => IdentifierToOwner(identifier) is not null;

    #endregion
}
