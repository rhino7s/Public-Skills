using System.Text;

namespace SqlServerReadonlyMcp.Sql;

internal static class DefinitionSearchHelper
{
    public static DefinitionSearchSelection Select(
        IReadOnlyList<string> lines,
        string searchText,
        int matchOffset,
        int maximumMatches,
        int maximumBytes)
    {
        var matchingLines = Enumerable.Range(0, lines.Count)
            .Where(index => lines[index].Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .Select(index => new DefinitionLineMatch(
                index,
                CountOccurrences(lines[index], searchText)))
            .ToArray();
        var actualOffset = Math.Max(0, matchOffset);
        var matches = new List<ObjectDefinitionMatch>();
        var usedBytes = 0;

        foreach (var match in matchingLines.Skip(actualOffset).Take(maximumMatches))
        {
            var text = lines[match.Index];
            var bytes = Encoding.UTF8.GetByteCount(text) + 64;
            if (usedBytes + bytes > maximumBytes)
            {
                return new DefinitionSearchSelection(
                    matches,
                    matchingLines.Length,
                    true,
                    actualOffset + matches.Count,
                    "max_result_size",
                    matches.Count == 0);
            }

            matches.Add(new ObjectDefinitionMatch(match.Index + 1, text, match.OccurrenceCount));
            usedBytes += bytes;
        }

        var nextOffset = actualOffset + matches.Count;
        var hasMore = nextOffset < matchingLines.Length;
        return new DefinitionSearchSelection(
            matches,
            matchingLines.Length,
            hasMore,
            hasMore ? nextOffset : null,
            hasMore ? "max_matches" : null,
            false);
    }

    private static int CountOccurrences(string value, string searchText)
    {
        var count = 0;
        var startIndex = 0;
        while (startIndex < value.Length)
        {
            var index = value.IndexOf(searchText, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            count++;
            startIndex = index + searchText.Length;
        }

        return count;
    }

    private sealed record DefinitionLineMatch(int Index, int OccurrenceCount);
}

internal sealed record DefinitionSearchSelection(
    IReadOnlyList<ObjectDefinitionMatch> Matches,
    int MatchCount,
    bool HasMore,
    int? NextMatchOffset,
    string? TruncationReason,
    bool OversizedFirstMatch);
