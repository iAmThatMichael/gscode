
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog;

namespace GSCode.Parser.Data;

public sealed class DocumentHoversLibrary
{
    private SortedList<int, IHoverable>?[] BackingHovers { get; }

    public DocumentHoversLibrary(int lineCount)
    {
        BackingHovers = new SortedList<int, IHoverable>?[lineCount];
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

        SortedList<int, IHoverable>? lineList = BackingHovers[location.Line];

        if(lineList is null)
        {
            Log.Debug("[HOVER] No hoverables on line {Line}", location.Line);
            return null;
        }

        Log.Debug("[HOVER] Found {Count} hoverables on line {Line}", lineList.Count, location.Line);

        IHoverable? bestMatch = null;
        int bestMatchSize = int.MaxValue;
        int matchCount = 0;

        foreach(KeyValuePair<int, IHoverable> hoverableKvp in lineList)
        {
            IHoverable hoverable = hoverableKvp.Value;

            Log.Debug("[HOVER] Checking hoverable: Type={Type}, Range=[{StartChar}-{EndChar}), Size={Size}", 
                hoverable.GetType().Name,
                hoverable.Range.Start.Character, 
                hoverable.Range.End.Character,
                hoverable.Range.End.Character - hoverable.Range.Start.Character);

            // Use standard LSP range convention: [start, end) - exclusive at end
            // This means hovering on delimiters like ')', ']', '>' will show no hover
            if(hoverable.Range.Start.Character <= location.Character &&
                hoverable.Range.End.Character > location.Character)
            {
                matchCount++;
                // Calculate the size of this range
                int rangeSize = hoverable.Range.End.Character - hoverable.Range.Start.Character;

                Log.Debug("[HOVER]   MATCH #{MatchNum}: Type={Type}, Range=[{StartChar}-{EndChar}), Size={Size}", 
                    matchCount,
                    hoverable.GetType().Name,
                    hoverable.Range.Start.Character, 
                    hoverable.Range.End.Character,
                    rangeSize);

                // Keep the smallest (most specific) range
                if (rangeSize < bestMatchSize)
                {
                    if (bestMatch != null)
                    {
                        Log.Debug("[HOVER]   Replacing previous best match (Type={OldType}, Size={OldSize}) with smaller match (Type={NewType}, Size={NewSize})",
                            bestMatch.GetType().Name,
                            bestMatchSize,
                            hoverable.GetType().Name,
                            rangeSize);
                    }
                    else
                    {
                        Log.Debug("[HOVER]   First match, setting as best match");
                    }

                    bestMatch = hoverable;
                    bestMatchSize = rangeSize;
                }
                else
                {
                    Log.Debug("[HOVER]   Skipping (larger than current best: {BestSize} < {CurrentSize})", 
                        bestMatchSize, rangeSize);
                }
            }
            else
            {
                Log.Debug("[HOVER]   No match: position {Pos} is outside range [{Start}-{End})", 
                    location.Character,
                    hoverable.Range.Start.Character,
                    hoverable.Range.End.Character);
            }
        }

        if (bestMatch != null)
        {
            Log.Information("[HOVER] Returning best match: Type={Type}, Range=[{StartChar}-{EndChar}), Size={Size}, TotalMatches={MatchCount}",
                bestMatch.GetType().Name,
                bestMatch.Range.Start.Character,
                bestMatch.Range.End.Character,
                bestMatchSize,
                matchCount);
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
    /// <exception cref="InvalidDataException"></exception>
    public void Add(IHoverable hoverable)
    {
        //if(hoverable.Range.SpansMultipleLines())
        //{
        //    throw new InvalidDataException("DocumentHoversLibrary does not support hoverables whose ranges span over multiple lines.");
        //}

        int line = hoverable.Range.Start.Line;

        SortedList<int, IHoverable>? lineList = BackingHovers[line];

        if (lineList is null)
        {
            lineList = new();
            BackingHovers[line] = lineList;
        }

        // An edge case exists where if a macro expands a macro to two instances they will collide. This prevents that being an issue.
        // We cannot assume there exists a one-to-one mapping between unique macro uses and the amount of expansions they produce.
        if(!lineList.ContainsKey(hoverable.Range.Start.Character))
        {
            lineList.Add(hoverable.Range.Start.Character, hoverable);
        }
    }
}
