using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;
using System.Collections.Concurrent;
using System.Text;

namespace GSCode.NET.LSP;

public class ScriptCache
{
    private ConcurrentDictionary<Uri, StringBuilder> Scripts { get; } = new(UriComparer.OrdinalIgnoreCase);

    public string AddToCache(TextDocumentItem document)
    {
        Uri documentUri = document.Uri.ToUri();
        Scripts[documentUri] = new(document.Text);

        return document.Text;
    }

    public string UpdateCache(OptionalVersionedTextDocumentIdentifier document, IEnumerable<TextDocumentContentChangeEvent> changes)
    {
        Uri documentUri = document.Uri.ToUri();
        StringBuilder cachedVersion = Scripts[documentUri];

        foreach (TextDocumentContentChangeEvent change in changes)
        {
            // If no range is specified then this is an outright replacement of the entire document.
            // This happens when client sends full sync (fallback) or on certain operations.
            if (change.Range == null)
            {
                cachedVersion = new(change.Text);
                Scripts[documentUri] = cachedVersion;
                continue;
            }

            Position start = change.Range.Start;
            Position end = change.Range.End;

            // Apply incremental change: replace text in the specified range
            int startLineBase = GetBaseCharOfLine(cachedVersion, start.Line);
            int endLineBase = GetBaseCharOfLine(cachedVersion, end.Line);

            // Validate positions
            if (startLineBase == -1)
            {
                // Start line doesn't exist - shouldn't happen, but fall back to appending
                Log.Warning("Incremental update: start line {Line} not found in document {Uri}", start.Line, documentUri);
                cachedVersion.Append(change.Text);
                continue;
            }

            int startPosition = startLineBase + start.Character;

            // Handle edge case: end position beyond buffer or end line doesn't exist
            if (endLineBase == -1 || startPosition > cachedVersion.Length)
            {
                // Clamp to end of document
                if (startPosition > cachedVersion.Length)
                {
                    startPosition = cachedVersion.Length;
                }
                cachedVersion.Remove(startPosition, cachedVersion.Length - startPosition);
                cachedVersion.Append(change.Text);
                continue;
            }

            int endPosition = endLineBase + end.Character;
            if (endPosition > cachedVersion.Length)
            {
                endPosition = cachedVersion.Length;
            }

            // Ensure valid range
            if (startPosition > endPosition)
            {
                Log.Warning("Incremental update: invalid range [{Start},{End}) in document {Uri}", startPosition, endPosition, documentUri);
                continue;
            }

            // Standard incremental update: remove old text and insert new
            cachedVersion.Remove(startPosition, endPosition - startPosition);
            cachedVersion.Insert(startPosition, change.Text);
        }

        // Ensure the updated StringBuilder is stored back
        Scripts[documentUri] = cachedVersion;
        return cachedVersion.ToString();
    }

    private static int GetBaseCharOfLine(StringBuilder sb, int line)
    {
        if (line == 0) return 0;

        int pos = 0;
        int currentLine = 0;

        while (currentLine < line && pos < sb.Length)
        {
            int newlinePos = IndexOf(sb, '\n', pos);
            if (newlinePos == -1) return -1;
            pos = newlinePos + 1;
            currentLine++;
        }

        return currentLine == line ? pos : -1;
    }

    private static int IndexOf(StringBuilder sb, char ch, int startIndex)
    {
        for (int i = startIndex; i < sb.Length; i++)
        {
            if (sb[i] == ch) return i;
        }
        return -1;
    }

    private static int GetBaseCharOfLine(string content, int line)
    {
        // Line 0 starts at position 0
        if (line == 0)
        {
            return 0;
        }

        int pos = 0;
        int currentLine = 0;

        while (currentLine < line && pos < content.Length)
        {
            // Find the next line break — handle \r\n, \n, and bare \r.
            // VS Code always sends LF (\n) line endings regardless of the OS,
            // so we must not rely on Environment.NewLine (which is \r\n on Windows).
            int newlinePos = content.IndexOf('\n', pos);
            if (newlinePos == -1)
            {
                // No more newlines found, return -1 to indicate line doesn't exist
                return -1;
            }
            // Move past the \n
            pos = newlinePos + 1;
            currentLine++;
        }

        return currentLine == line ? pos : -1;
    }

    public void RemoveFromCache(TextDocumentIdentifier document)
    {
        Uri documentUri = document.Uri.ToUri();
        Scripts.Remove(documentUri, out StringBuilder? _);
    }

    /// <summary>
    /// Retrieves the current cached content for the given document URI.
    /// Returns false if the document is not in the cache.
    /// </summary>
    public bool TryGetContent(Uri uri, out string content)
    {
        if (Scripts.TryGetValue(uri, out StringBuilder? sb))
        {
            content = sb.ToString();
            return true;
        }

        content = string.Empty;
        return false;
    }
}
