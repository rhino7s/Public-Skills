using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class DefinitionSearchHelperTests
{
    [Fact]
    public void ReturnsCaseInsensitiveMatchingLinesAndOccurrenceCounts()
    {
        string[] lines =
        [
            "CREATE PROCEDURE dbo.Example",
            "AS",
            "SELECT Revenue",
            "FROM dbo.SourceTable",
            "WHERE revenue IS NOT NULL OR REVENUE = 0;",
            "RETURN;",
        ];

        var result = DefinitionSearchHelper.Select(lines, "REVENUE", 0, 20, 4_096);

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(3, result.Matches[0].Line);
        Assert.Equal(1, result.Matches[0].OccurrenceCount);
        Assert.Equal(5, result.Matches[1].Line);
        Assert.Equal(2, result.MatchCount);
        Assert.False(result.HasMore);
        Assert.Null(result.NextMatchOffset);
        Assert.Null(result.TruncationReason);
        Assert.False(result.OversizedFirstMatch);
    }

    [Fact]
    public void PagesMatchingLinesByOffset()
    {
        string[] lines = ["value", "skip", "value", "skip", "value"];

        var result = DefinitionSearchHelper.Select(lines, "value", 1, 1, 4_096);

        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.Line);
        Assert.Equal(3, result.MatchCount);
        Assert.True(result.HasMore);
        Assert.Equal(2, result.NextMatchOffset);
        Assert.Equal("max_matches", result.TruncationReason);
    }

    [Fact]
    public void RejectsFirstSnippetThatExceedsByteBudget()
    {
        string[] lines = [new string('x', 100)];

        var result = DefinitionSearchHelper.Select(lines, "x", 0, 20, 10);

        Assert.Empty(result.Matches);
        Assert.True(result.HasMore);
        Assert.Equal(0, result.NextMatchOffset);
        Assert.Equal("max_result_size", result.TruncationReason);
        Assert.True(result.OversizedFirstMatch);
    }
}
