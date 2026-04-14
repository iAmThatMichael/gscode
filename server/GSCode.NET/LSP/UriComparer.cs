namespace GSCode.NET.LSP;

/// <summary>
/// Case-insensitive URI equality comparer for file-path URIs.
/// VS Code normalises drive letters to lowercase on Windows; our server may produce
/// uppercase drive letters via <c>new Uri(path)</c>. OrdinalIgnoreCase prevents
/// cache misses caused by that difference.
/// </summary>
internal sealed class UriComparer : IEqualityComparer<Uri>
{
    public static readonly UriComparer OrdinalIgnoreCase = new();

    private UriComparer() { }

    public bool Equals(Uri? x, Uri? y)
    {
        if (x is null && y is null) return true;
        if (x is null || y is null) return false;
        return string.Equals(x.AbsoluteUri, y.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(Uri uri)
        => uri.AbsoluteUri.ToLowerInvariant().GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// Helpers for constructing <see cref="Uri"/> values from file-system paths.
/// </summary>
internal static class UriHelper
{
    /// <summary>
    /// Converts an absolute file-system path (Windows or Unix) to a <c>file:///</c> URI.
    /// </summary>
    public static Uri FromFilePath(string filePath)
        => new Uri(Path.GetFullPath(filePath));

    /// <summary>
    /// Extracts a valid local file-system path from a <c>file://</c> URI.
    /// <see cref="Uri.LocalPath"/> returns <c>/C:/foo</c> on Windows which
    /// is not recognised by most IO APIs. This helper strips the leading
    /// slash when a Windows drive letter follows.
    /// </summary>
    public static string GetLocalPath(Uri uri)
    {
        string path = uri.LocalPath;
        // Uri.LocalPath for "file:///C:/foo" → "/C:/foo" on Windows
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..];
        return path;
    }
}
