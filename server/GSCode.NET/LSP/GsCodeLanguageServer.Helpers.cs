using System.IO;

namespace GSCode.NET.LSP;

public sealed partial class GsCodeLanguageServer
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool IsScriptFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".gsc" or ".csc" or ".gsh";
    }

    private static bool IsInProtectedRawFolder(string filePath)
    {
        try
        {
            string norm = Path.GetFullPath(filePath).Replace('/', '\\').ToLowerInvariant();

            string? custom = GSCode.Parser.Configuration.CompletionConfiguration.CustomRawPath;
            if (!string.IsNullOrEmpty(custom))
            {
                string nc = Path.GetFullPath(custom).Replace('/', '\\').ToLowerInvariant();
                if (norm.StartsWith(nc)) return true;
            }

            string? taGame = Environment.GetEnvironmentVariable("TA_GAME_PATH");
            if (!string.IsNullOrEmpty(taGame))
            {
                string shareRaw = Path.Combine(taGame, "share", "raw");
                if (Directory.Exists(shareRaw))
                {
                    string ns = Path.GetFullPath(shareRaw).Replace('/', '\\').ToLowerInvariant();
                    if (norm.StartsWith(ns)) return true;
                }
            }
        }
        catch { /* ignore path errors */ }
        return false;
    }
}
