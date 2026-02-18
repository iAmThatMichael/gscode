using System;
using System.Collections.Generic;

namespace GSCode.Parser.Util;

/// <summary>
/// Utility methods for sanitizing and normalizing doc comments for display in hover/completion.
/// </summary>
internal static class DocCommentHelper
{
    /// <summary>
    /// Sanitizes a raw doc comment string for Markdown display.
    /// Strips comment wrappers, normalizes line endings, handles escape sequences,
    /// and protects against Markdown injection.
    /// </summary>
    /// <param name="raw">The raw doc comment text</param>
    /// <returns>Cleaned markdown-safe text</returns>
    public static string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string s = raw.Trim();
        
        // Strip common block wrappers: /@ @/ or /* */
        if (s.StartsWith("/@"))
        {
            if (s.EndsWith("@/")) s = s.Substring(2, s.Length - 4);
            else s = s.Substring(2);
        }
        else if (s.StartsWith("/*"))
        {
            if (s.EndsWith("*/")) s = s.Substring(2, s.Length - 4);
            else s = s.Substring(2);
        }

        // Normalize CRLF to LF and unescape common sequences (\\n, \\r, \\t, \\")
        s = s.Replace("\r\n", "\n").Replace("\r", "\n");
        s = s.Replace("\\n", "\n").Replace("\\r", string.Empty)
             .Replace("\\t", "    ").Replace("\\\"", "\"");

        // Split and clean each line: remove leading *, trim, remove surrounding quotes per line
        string[] rawLines = s.Split('\n');
        List<string> lines = new();
        foreach (var line in rawLines)
        {
            string l = line.Trim();
            if (l.StartsWith("*")) l = l.TrimStart('*').TrimStart();
            if (l.Length >= 2 && l[0] == '"' && l[^1] == '"')
            {
                l = l.Substring(1, l.Length - 2);
            }
            // Avoid stray Markdown code fence terminators
            l = l.Replace("```", "`\u200B``");
            if (l.Length > 0) lines.Add(l);
        }
        
        return string.Join("\n", lines);
    }
}
