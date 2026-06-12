using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Util;

/// <summary>
/// Shared logic for reasoning about <c>#using</c> directives: extracting them from source,
/// converting file paths to directive paths, and computing where a new directive should be
/// inserted. Scripts load in alphabetical order in the game, so the convention is to keep
/// <c>#using</c> directives sorted alphabetically — insertions follow that order.
/// </summary>
public static class UsingDirectiveHelper
{
    /// <summary>
    /// An existing <c>#using</c> directive found in a script: its path text (e.g.
    /// <c>scripts\shared\util_shared</c>) and the zero-based line it starts on.
    /// </summary>
    public readonly record struct UsingDirective(string Path, int Line);

    /// <summary>
    /// Converts an absolute or relative script file path to the <c>#using</c> directive path
    /// format: everything from the <c>scripts/</c> directory onward, extension stripped,
    /// backslash-separated (e.g. <c>scripts\shared\ai\zombie_utility</c>).
    /// Returns <see langword="null"/> when the path has no <c>scripts/</c> segment.
    /// </summary>
    public static string? ConvertToUsingPath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        string normalised = filePath.Replace('\\', '/');

        string relativePath;
        if (normalised.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
        {
            relativePath = normalised;
        }
        else
        {
            const string marker = "/scripts/";
            int idx = normalised.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return null;
            }

            relativePath = normalised[(idx + 1)..]; // keep the "scripts/" prefix
        }

        // Remove the file extension (.gsc, .csc, .gsh)
        int dotIndex = relativePath.LastIndexOf('.');
        if (dotIndex > relativePath.LastIndexOf('/'))
        {
            relativePath = relativePath[..dotIndex];
        }

        return relativePath.Replace('/', '\\');
    }

    /// <summary>
    /// Extracts all <c>#using</c> directives from raw document text.
    /// </summary>
    public static List<UsingDirective> ExtractUsingsFromContent(string content)
    {
        var result = new List<UsingDirective>();
        if (string.IsNullOrEmpty(content))
        {
            return result;
        }

        string[] lines = content.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("#using", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string path = trimmed["#using".Length..].Trim();
            int semicolon = path.IndexOf(';');
            if (semicolon >= 0)
            {
                path = path[..semicolon];
            }
            // Strip a trailing line comment if present
            int comment = path.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0)
            {
                path = path[..comment];
            }

            path = path.Trim().Replace('/', '\\');
            if (path.Length > 0)
            {
                result.Add(new UsingDirective(path, i));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="usings"/> already contains the given directive path
    /// (case-insensitive, separator-insensitive).
    /// </summary>
    public static bool ContainsUsing(IEnumerable<UsingDirective> usings, string usingPath)
    {
        string normalised = usingPath.Replace('/', '\\');
        foreach (var u in usings)
        {
            if (string.Equals(u.Path, normalised, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Computes the position at which a new <c>#using</c> directive should be inserted so the
    /// directive list stays alphabetically sorted. Inserts before the first existing directive
    /// that sorts after the new path; if none does, after the last directive. When the file has
    /// no directives, returns <paramref name="fallback"/> (typically the top of the file).
    /// The returned position is a start-of-line insertion point for the text
    /// <c>#using path;\n</c>.
    /// </summary>
    public static Position GetAlphabeticalInsertPosition(
        IReadOnlyList<UsingDirective> usings, string newUsingPath, Position? fallback = null)
    {
        if (usings.Count == 0)
        {
            return fallback ?? new Position { Line = 0, Character = 0 };
        }

        string normalised = newUsingPath.Replace('/', '\\');

        foreach (var existing in usings)
        {
            if (string.Compare(normalised, existing.Path, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return new Position { Line = existing.Line, Character = 0 };
            }
        }

        // Sorts after every existing directive — insert on the line following the last one.
        int lastLine = usings[^1].Line;
        return new Position { Line = lastLine + 1, Character = 0 };
    }
}
