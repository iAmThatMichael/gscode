
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;

namespace GSCode.Parser.Data;

public sealed class DocumentHoversLibrary
{
    private Dictionary<int, SortedList<int, IHoverable>> BackingHovers { get; } = new();

    public DocumentHoversLibrary(int lineCount)
    {
        // lineCount hint no longer needed with Dictionary
    }

    /// <summary>
    /// Gets the hover that corresponds to the given position if it exists.
    /// When multiple overlapping ranges contain the position, returns the most specific (smallest) one.
    /// </summary>
    /// <param name="location">The target location</param>
    /// <returns>An IHoverable instance corresponding to the position if it exists, or null otherwise</returns>
    public IHoverable? Get(Position location)
    {
        Log.Debug("[HOVER] Requesting hover at position Line={Line} Char={Char}", location.Line, location.Character);

        if (!BackingHovers.TryGetValue(location.Line, out var lineList))
        {
            Log.Debug("[HOVER] No hoverables on line {Line}", location.Line);
            return null;
        }

        Log.Debug("[HOVER] Found {Count} hoverables on line {Line}", lineList.Count, location.Line);

        IHoverable? bestMatch = null;
        int bestMatchSize = int.MaxValue;

        // Binary search to find the last entry whose start character is <= cursor position.
        // Then scan backwards from there: any earlier-starting hoverable whose range still
        // encloses the cursor is also a valid candidate (overlapping ranges, different starts).
        var keys = lineList.Keys;
        int lo = 0, hi = keys.Count - 1;
        int bestIdx = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (keys[mid] <= location.Character)
            {
                bestIdx = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for (int i = bestIdx; i >= 0; i--)
        {
            IHoverable hoverable = lineList.Values[i];

            Log.Debug("[HOVER] Checking hoverable: Type={Type}, Range=[{StartChar}-{EndChar}), Size={Size}", 
                hoverable.GetType().Name,
                hoverable.Range.Start.Character, 
                hoverable.Range.End.Character,
                hoverable.Range.End.Character - hoverable.Range.Start.Character);

            // Use standard LSP range convention: [start, end) - exclusive at end
            // This means hovering on delimiters like ')', ']', '>' will show no hover
            if (hoverable.Range.Start.Character <= location.Character &&
                hoverable.Range.End.Character > location.Character)
            {
                // Calculate the size of this range
                int rangeSize = hoverable.Range.End.Character - hoverable.Range.Start.Character;

                // Keep the smallest (most specific) range
                if (rangeSize < bestMatchSize)
                {
                    bestMatch = hoverable;
                    bestMatchSize = rangeSize;
                }
            }
            else if (hoverable.Range.End.Character <= location.Character)
            {
                // Earlier entries can only have smaller end characters; no point continuing.
                break;
            }
        }

        if (bestMatch != null)
        {
            Log.Debug("[HOVER] Returning best match: Type={Type}, Range=[{StartChar}-{EndChar}), Size={Size}",
                bestMatch.GetType().Name,
                bestMatch.Range.Start.Character,
                bestMatch.Range.End.Character,
                bestMatchSize);
        }
        else
        {
            Log.Debug("[HOVER] No matching hoverables found for position {Pos} on line {Line}", 
                location.Character, location.Line);
        }

        return bestMatch;
    }

    /// <summary>
    /// Adds the specified hoverable to the hover library. The hoverable's range must not span over multiple lines.
    /// </summary>
    /// <param name="hoverable">IHoverable to add</param>
    public void Add(IHoverable hoverable)
    {
        int line = hoverable.Range.Start.Line;

        if (!BackingHovers.TryGetValue(line, out var lineList))
        {
            lineList = new();
            BackingHovers[line] = lineList;
        }

        // An edge case exists where if a macro expands a macro to two instances they will collide.
        lineList.TryAdd(hoverable.Range.Start.Character, hoverable);
    }
}
