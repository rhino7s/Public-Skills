using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class ObjectReferenceSearchHelperTests
{
    [Fact]
    public void SameDatabaseAllowsQualifiedAndLongBareNames()
    {
        var target = Target("ExampleDatabase", "dbo", "ExampleReferenceTarget", "U");
        var pattern = ObjectReferenceSearchHelper.CreatePattern(target, "ExampleDatabase");
        var selection = ObjectReferenceSearchHelper.FindMatches(
            "SELECT * FROM dbo.ExampleReferenceTarget;\n" +
            "DELETE FROM [ExampleReferenceTarget];\n" +
            "SELECT ExampleReferenceTargetArchive;",
            pattern);

        Assert.Equal(2, selection.OccurrenceCount);
        Assert.Equal([1, 2], selection.Matches.Select(item => item.Line).ToArray());
    }

    [Fact]
    public void ShortNameRequiresSchemaQualificationInSameDatabase()
    {
        var target = Target("ExampleDatabase", "dbo", "ET", "U");
        var pattern = ObjectReferenceSearchHelper.CreatePattern(target, "ExampleDatabase");
        var selection = ObjectReferenceSearchHelper.FindMatches(
            "SELECT * FROM ET;\nSELECT * FROM dbo.ET;",
            pattern);

        var match = Assert.Single(selection.Matches);
        Assert.Equal(2, match.Line);
        Assert.Equal(1, selection.OccurrenceCount);
    }

    [Fact]
    public void DifferentDatabaseRequiresThreePartName()
    {
        var target = Target("CoreDb", "dbo", "CustomerLedger", "U");
        var pattern = ObjectReferenceSearchHelper.CreatePattern(target, "ReportingDb");
        var selection = ObjectReferenceSearchHelper.FindMatches(
            "SELECT * FROM dbo.CustomerLedger;\n" +
            "SELECT * FROM [CoreDb].[dbo].[CustomerLedger];",
            pattern);

        var match = Assert.Single(selection.Matches);
        Assert.Equal(2, match.Line);
        Assert.Equal(1, selection.OccurrenceCount);
    }

    [Fact]
    public void PaginationReturnsUsableNextOffsetWithinBoundary()
    {
        var state = ObjectReferenceSearchHelper.CreatePagination(950, 50, true, 1_000);

        Assert.Equal(1_000, state.NextOffset);
        Assert.Null(state.TruncationReason);
    }

    [Fact]
    public void PaginationDoesNotReturnOffsetBeyondBoundary()
    {
        var state = ObjectReferenceSearchHelper.CreatePagination(1_000, 50, true, 1_000);

        Assert.Null(state.NextOffset);
        Assert.Equal("max_offset", state.TruncationReason);
    }

    [Fact]
    public void PaginationOmitsCursorWhenResultsAreComplete()
    {
        var state = ObjectReferenceSearchHelper.CreatePagination(20, 10, false, 1_000);

        Assert.Null(state.NextOffset);
        Assert.Null(state.TruncationReason);
    }

    private static ObjectIdentity Target(string database, string schema, string name, string type) =>
        new(database, schema, name, type, "TEST", false);
}
