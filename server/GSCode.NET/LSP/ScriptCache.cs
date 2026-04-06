using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;
using System.Collections.Concurrent;
using System.Text;

namespace GSCode.NET.LSP;

public class ScriptCache
{
    private ConcurrentDictionary<DocumentUri, StringBuilder> Scripts { get; } = new();

    public string AddToCache(TextDocumentItem document)
    {
        DocumentUri documentUri = document.Uri;
        Scripts[documentUri] = new(document.Text);

        return document.Text;
    }

    public string UpdateCache(TextDocumentIdentifier document, IEnumerable<TextDocumentContentChangeEvent> changes)
    {
        DocumentUri documentUri = document.Uri;
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
            string cachedString = cachedVersion.ToString();
            int startLineBase = GetBaseCharOfLine(cachedString, start.Line);
            int endLineBase = GetBaseCharOfLine(cachedString, end.Line);

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
            int newlinePos = content.IndexOf(Environment.NewLine, pos);
            if (newlinePos == -1)
            {
                // No more newlines found, return -1 to indicate line doesn't exist
                return -1;
            }
            // Move past the newline (Environment.NewLine could be \r\n or \n)
            pos = newlinePos + Environment.NewLine.Length;
            currentLine++;
        }

        return currentLine == line ? pos : -1;
    }

    public void RemoveFromCache(TextDocumentIdentifier document)
    {
        DocumentUri documentUri = document.Uri;
        Scripts.Remove(documentUri, out StringBuilder? _);
    }
}
