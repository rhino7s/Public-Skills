using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class SqlErrorClassifierTests
{
    [Theory]
    [InlineData(-2, "timeout")]
    [InlineData(229, "permission_denied")]
    [InlineData(916, "permission_denied")]
    [InlineData(4060, "authentication_or_database")]
    [InlineData(18456, "authentication_or_database")]
    [InlineData(20, "connection_error")]
    [InlineData(53, "connection_error")]
    [InlineData(208, "sql_error")]
    [InlineData(2812, "sql_error")]
    [InlineData(50000, "sql_error")]
    public void CategorizesConsistently(int errorNumber, string expectedCategory)
    {
        Assert.Equal(expectedCategory, SqlErrorClassifier.Categorize(errorNumber));
    }
}
