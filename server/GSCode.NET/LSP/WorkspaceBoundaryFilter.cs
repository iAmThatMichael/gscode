using Microsoft.VisualStudio.LanguageServer.Protocol;
using Serilog;

namespace GSCode.NET.LSP;

/// <summary>
/// Centralized workspace boundary filtering for symbol definitions.
/// Priority order: Local workspace > CustomRawPath > TA_TOOLS_PATH > Skip
/// </summary>
public static class WorkspaceBoundaryFilter
{
    /// <summary>
    /// Result of filtering a symbol location.
    /// </summary>
    public enum FilterResult
    {
        /// <summary>
        /// Symbol is in the local workspace - highest priority, return immediately.
        /// </summary>
        LocalWorkspace,
        
        /// <summary>
        /// Symbol is in CustomRawPath - save as high priority candidate.
        /// </summary>
        CustomRawPath,
        
        /// <summary>
        /// Symbol is in TA_TOOLS_PATH - save as low priority candidate.
        /// </summary>
        ToolsRawPath,
        
        /// <summary>
        /// Symbol is in a different workspace - skip it.
        /// </summary>
        DifferentWorkspace,
        
        /// <summary>
        /// No workspace context or source is in raw folder - include without filtering.
        /// </summary>
        NoFiltering
    }
    
    /// <summary>
    /// Filter result with location information.
    /// </summary>
    public readonly record struct FilteredLocation(
        FilterResult Result,
        Location? Location
    );
    
    /// <summary>
    /// Filters a symbol location based on workspace boundaries.
    /// </summary>
    /// <param name="symbolFilePath">Path to the file containing the symbol</param>
    /// <param name="symbolRange">Range of the symbol in the file</param>
    /// <param name="currentWorkspaceRoot">Workspace root of the requesting file (null if no filtering)</param>
    /// <param name="currentIsInRawFolder">Whether the requesting file is in a raw folder</param>
    /// <returns>Filter result indicating priority level</returns>
    public static FilterResult FilterSymbolLocation(
        string symbolFilePath,
        string? currentWorkspaceRoot,
        bool currentIsInRawFolder)
    {
        // If no workspace context or current file is in raw folder, don't filter
        if (currentWorkspaceRoot == null || currentIsInRawFolder)
        {
            return FilterResult.NoFiltering;
        }
        
        string? symbolWorkspaceRoot = GetScriptsWorkspaceRoot(symbolFilePath);
        
        // Priority 1: Same local workspace
        if (symbolWorkspaceRoot != null && 
            symbolWorkspaceRoot.Equals(currentWorkspaceRoot, StringComparison.OrdinalIgnoreCase))
        {
            return FilterResult.LocalWorkspace;
        }
        
        // Priority 2: CustomRawPath
        if (IsInCustomRawFolder(symbolFilePath))
        {
            return FilterResult.CustomRawPath;
        }
        
        // Priority 3: TA_TOOLS_PATH
        if (IsInToolsRawFolder(symbolFilePath))
        {
            return FilterResult.ToolsRawPath;
        }
        
        // Different workspace - skip
        return FilterResult.DifferentWorkspace;
    }
    
    /// <summary>
    /// Filters a list of symbol definitions and returns them grouped by priority.
    /// </summary>
    public static FilteredSymbols FilterSymbolDefinitions(
        IEnumerable<(string Namespace, string Name, string FilePath, Range Range)> symbols,
        string? currentWorkspaceRoot,
        bool currentIsInRawFolder)
    {
        var result = new FilteredSymbols();
        
        foreach (var symbol in symbols)
        {
            var filterResult = FilterSymbolLocation(symbol.FilePath, currentWorkspaceRoot, currentIsInRawFolder);
            
            switch (filterResult)
            {
                case FilterResult.LocalWorkspace:
                    result.LocalWorkspace.Add(symbol);
                    break;
                case FilterResult.CustomRawPath:
                    result.CustomRawPath.Add(symbol);
                    break;
                case FilterResult.ToolsRawPath:
                    result.ToolsRawPath.Add(symbol);
                    break;
                case FilterResult.NoFiltering:
                    result.NoFiltering.Add(symbol);
                    break;
                case FilterResult.DifferentWorkspace:
                    result.Filtered++;
                    break;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Extracts the workspace root from a file path by finding the scripts folder.
    /// </summary>
    public static string? GetScriptsWorkspaceRoot(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        string normalized = NormalizePathForComparison(filePath);

        int scriptsIndex = normalized.LastIndexOf("/scripts/", StringComparison.OrdinalIgnoreCase);
        if (scriptsIndex == -1)
        {
            return null;
        }

        return normalized.Substring(0, scriptsIndex);
    }

    /// <summary>
    /// Checks if a file path is within the CustomRawPath.
    /// </summary>
    public static bool IsInCustomRawFolder(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        string? customRawPath = GSCode.Parser.Configuration.CompletionConfiguration.CustomRawPath;
        if (string.IsNullOrEmpty(customRawPath))
            return false;

        string normalizedFile = NormalizePathForComparison(filePath);
        string normalizedCustom = NormalizePathForComparison(customRawPath);

        return normalizedFile.StartsWith(normalizedCustom, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a file path is within the TA_TOOLS_PATH raw folder.
    /// </summary>
    public static bool IsInToolsRawFolder(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        string? toolsPath = Environment.GetEnvironmentVariable("TA_TOOLS_PATH");
        if (string.IsNullOrEmpty(toolsPath))
            return false;

        string normalizedFile = NormalizePathForComparison(filePath);
        string rawPath = NormalizePathForComparison(Path.Combine(toolsPath, "share", "raw"));

        return normalizedFile.StartsWith(rawPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a file path for comparison.
    /// </summary>
    public static string NormalizePathForComparison(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        string normalized = path.Replace('\\', '/');

        // Remove leading slash before drive letter on Windows: "/G:/" -> "G:/"
        if (normalized.Length >= 3 && normalized[0] == '/' && normalized[2] == ':')
        {
            normalized = normalized.Substring(1);
        }

        return normalized;
    }
}

/// <summary>
/// Container for filtered symbols grouped by priority.
/// </summary>
public class FilteredSymbols
{
    public List<(string Namespace, string Name, string FilePath, Range Range)> LocalWorkspace { get; } = new();
    public List<(string Namespace, string Name, string FilePath, Range Range)> CustomRawPath { get; } = new();
    public List<(string Namespace, string Name, string FilePath, Range Range)> ToolsRawPath { get; } = new();
    public List<(string Namespace, string Name, string FilePath, Range Range)> NoFiltering { get; } = new();
    public int Filtered { get; set; }
    
    /// <summary>
    /// Gets all symbols in priority order: LocalWorkspace > CustomRawPath > ToolsRawPath > NoFiltering
    /// </summary>
    public IEnumerable<(string Namespace, string Name, string FilePath, Range Range)> GetInPriorityOrder()
    {
        return LocalWorkspace
            .Concat(CustomRawPath)
            .Concat(ToolsRawPath)
            .Concat(NoFiltering);
    }
    
    /// <summary>
    /// Gets the first symbol in priority order, or null if none found.
    /// </summary>
    public (string Namespace, string Name, string FilePath, Range Range)? GetFirst()
    {
        if (LocalWorkspace.Count > 0)
            return LocalWorkspace[0];
        if (CustomRawPath.Count > 0)
            return CustomRawPath[0];
        if (ToolsRawPath.Count > 0)
            return ToolsRawPath[0];
        if (NoFiltering.Count > 0)
            return NoFiltering[0];
        return null;
    }
}
