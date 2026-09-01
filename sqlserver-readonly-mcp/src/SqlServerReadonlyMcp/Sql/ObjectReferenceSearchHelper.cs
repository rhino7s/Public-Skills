using System.Text.RegularExpressions;

namespace SqlServerReadonlyMcp.Sql;

internal static class ObjectReferenceSearchHelper
{
    private const int MaximumSnippetCharacters = 300;
    private const int MaximumMatchesPerSource = 4;

    public static Regex CreatePattern(ObjectIdentity target, string searchDatabase)
    {
        var database = Identifier(target.Database);
        var schema = Identifier(target.Schema);
        var name = Identifier(target.Name);
        var separator = @"\s*\.\s*";
        var patterns = new List<string>
        {
            database + separator + schema + separator + name,
        };

        if (string.Equals(target.Schema, "dbo", StringComparison.OrdinalIgnoreCase))
        {
            patterns.Add(database + separator + @"\.\s*" + name);
        }

        if (string.Equals(target.Database, searchDatabase, StringComparison.OrdinalIgnoreCase))
        {
            patterns.Add(schema + separator + name);
            if (target.Name.Length >= 4)
            {
                patterns.Add(Identifier(target.Name, allowQuoted: false));
            }
        }

        return new Regex(
            string.Join('|', patterns),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));
    }

    public static ReferenceTextSelection FindMatches(string text, Regex pattern)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var matches = new List<ReferenceMatch>();
        var totalOccurrences = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var lineMatches = pattern.Matches(lines[index]);
            if (lineMatches.Count == 0)
            {
                continue;
            }

            totalOccurrences += lineMatches.Count;
            if (matches.Count >= MaximumMatchesPerSource)
            {
                continue;
            }

            matches.Add(new ReferenceMatch(
                index + 1,
                CreateSnippet(lines[index], lineMatches[0].Index, lineMatches[0].Length),
                lineMatches.Count));
        }

        return new ReferenceTextSelection(matches, totalOccurrences);
    }

    public static ReferencePaginationState CreatePagination(
        int offset,
        int returnedCount,
        bool hasMore,
        int maximumOffset)
    {
        if (!hasMore)
        {
            return new ReferencePaginationState(null, null);
        }

        var candidate = checked(offset + returnedCount);
        return candidate <= maximumOffset
            ? new ReferencePaginationState(candidate, null)
            : new ReferencePaginationState(null, "max_offset");
    }

    private static string Identifier(string value, bool allowQuoted = true)
    {
        var escaped = Regex.Escape(value);
        var bracketed = @"\[" + Regex.Escape(value.Replace("]", "]]", StringComparison.Ordinal)) + @"\]";
        var bare = @"(?<![\p{L}\p{N}_@$#])" + escaped + @"(?![\p{L}\p{N}_@$#])";
        if (!allowQuoted)
        {
            return "(?:" + bracketed + '|' + bare + ')';
        }

        var quoted = '"' + Regex.Escape(value.Replace("\"", "\"\"", StringComparison.Ordinal)) + '"';
        return "(?:" + bracketed + '|' + quoted + '|' + bare + ')';
    }

    private static string CreateSnippet(string line, int matchIndex, int matchLength)
    {
        if (line.Length <= MaximumSnippetCharacters)
        {
            return line;
        }

        var desiredStart = Math.Max(0, matchIndex - 120);
        var maximumStart = Math.Max(0, line.Length - MaximumSnippetCharacters);
        var start = Math.Min(desiredStart, maximumStart);
        var length = Math.Min(MaximumSnippetCharacters, line.Length - start);
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < line.Length ? "…" : string.Empty;
        return prefix + line.Substring(start, length) + suffix;
    }
}

internal sealed record ReferenceTextSelection(
    IReadOnlyList<ReferenceMatch> Matches,
    int OccurrenceCount);

internal sealed record ReferencePaginationState(
    int? NextOffset,
    string? TruncationReason);
