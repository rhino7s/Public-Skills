using System.Text.Json;
using SqlServerReadonlyMcp.Sql;
namespace SqlServerReadonlyMcp.Tests;
public sealed class DeliveryStatisticsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StatisticsUseOnlyDeliveredSetsAndPreserveStatus(bool success)
    {
        ResultSetResult set = new([new("中文", "nvarchar")], [["值"], [null]]);
        var result = SqlQueryService.FinalizeDelivery(new(success, "test", [set], 123, 0, 1, 2, true, "limit", null,
            success ? null : new("execution_unknown", "failed")));
        Assert.Equal(2, result.ReturnedRows);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(result.ResultSets, Program.CreateToolJsonOptions()).Length, result.ResultSizeBytes);
        Assert.Equal(success, result.Success);
        Assert.True(result.Truncated);
        Assert.Equal("limit", result.TruncationReason);
    }
    [Fact]
    public void NoSetsAndAnEmptySetHaveDifferentByteCounts()
    {
        QueryResult result = new(false, "test", [], 5, 99, 0, 0, false, null, null, new("canceled", "canceled"));
        Assert.Equal(0, SqlQueryService.FinalizeDelivery(result).ResultSizeBytes);
        Assert.Equal(0, SqlQueryService.FinalizeDelivery(result).ReturnedRows);
        result = result with { ResultSets = [new([new("x", "int")], [])] };
        Assert.True(SqlQueryService.FinalizeDelivery(result).ResultSizeBytes > 0);
        Assert.Equal(0, SqlQueryService.FinalizeDelivery(result).ReturnedRows);
    }
}
