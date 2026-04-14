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
}
