using Microsoft.VisualStudio.LanguageServer.Protocol;
using System.IO;
using Serilog;

namespace GSCode.Parser.Util;

/// <summary>
/// Public wrapper around ParserUtil.GetScriptFilePath for external access.
/// </summary>
public static class ScriptFileResolver
{
    /// <summary>
    /// Given the path of the current script file and a desired script path,
    /// returns the full file system path if found, checking:
    /// 1. Local workspace (extracted from current script path)
    /// 2. CustomRawPath (if configured)
    /// 3. TA_TOOLS_PATH (from environment variable)
    /// Returns null if the file doesn't exist in any location.
    /// </summary>
    public static string? GetScriptFilePath(string currentScriptPath, string desiredScriptPath)
    {
        return ParserUtil.GetScriptFilePath(currentScriptPath, desiredScriptPath);
    }

    /// <summary>
    /// Converts a path to relative script format (e.g., "scripts\shared\util_shared.gsc").
    /// If already relative, returns as-is. If absolute, extracts the "scripts/..." portion.
    /// Returns null if the path doesn't contain a scripts folder.
    /// </summary>
    public static string? ConvertToRelativeScriptPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Check if already relative (doesn't contain drive letter or start with /)
        if (!path.Contains(":") && !path.StartsWith("/"))
        {
            return path;
        }

        // Absolute path - extract scripts/... portion
        string normalized = path.Replace('\\', '/');

        // Remove leading slash before drive letter on Windows: "/G:/" -> "G:/"
        if (normalized.Length >= 3 && normalized[0] == '/' && normalized[2] == ':')
        {
            normalized = normalized.Substring(1);
        }

        // Find "scripts/" and extract from there
        int scriptsIndex = normalized.LastIndexOf("/scripts/", StringComparison.OrdinalIgnoreCase);
        if (scriptsIndex >= 0)
        {
            // Extract from "scripts/" onwards
            string relativePath = normalized.Substring(scriptsIndex + 1); // +1 to skip leading '/'
            return relativePath.Replace('/', '\\'); // Convert back to backslashes
        }

        // Couldn't convert, return null
        return null;
    }

    /// <summary>
    /// Resolves a definition location (filepath + range) to an absolute path Location.
    /// Handles backward compatibility by converting absolute paths to relative, then resolving.
    /// </summary>
    /// <param name="currentScriptPath">The path of the script making the request</param>
    /// <param name="targetFilePath">The file path to resolve (may be absolute or relative)</param>
    /// <param name="range">The range within the target file</param>
    /// <returns>A Location with resolved URI and range, or null if resolution fails</returns>
    public static Location? ResolveDefinitionLocation(string currentScriptPath, string targetFilePath, Range range)
    {
        // Convert to relative path if it's absolute (for backward compatibility with old indexed files)
        string pathToResolve = ConvertToRelativeScriptPath(targetFilePath) ?? targetFilePath;

        // Resolve relative path using existing dependency resolution logic
        string? resolvedPath = GetScriptFilePath(currentScriptPath, pathToResolve);

        if (resolvedPath != null)
        {
            string normalized = NormalizeFilePathForUri(resolvedPath);
            var targetUri = new Uri(normalized);
            return new Location() { Uri = targetUri, Range = range };
        }
        else
        {
            Log.Warning("ResolveDefinitionLocation: Could not resolve path: {RelativePath}", pathToResolve);
            return null;
        }
    }

    /// <summary>
    /// Normalizes a file path for URI creation.
    /// </summary>
    public static string NormalizeFilePathForUri(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return filePath;

        // Some paths are produced like "/g:/path/..." on Windows; remove leading slash if followed by drive letter
        if (filePath.Length >= 3 && filePath[0] == '/' && char.IsLetter(filePath[1]) && filePath[2] == ':')
        {
            filePath = filePath.Substring(1);
        }

        // Convert forward slashes to platform directory separator to be safe
        if (Path.DirectorySeparatorChar == '\\')
        {
            filePath = filePath.Replace('/', Path.DirectorySeparatorChar);
        }

        // Return full path if possible
        try
        {
            return Path.GetFullPath(filePath);
        }
        catch
        {
            return filePath;
        }
    }
}

