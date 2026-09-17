using System.Text.Json;
using ModelContextProtocol.Protocol;
using SqlServerReadonlyMcp.Sql;
using SqlServerReadonlyMcp.Tools;

namespace SqlServerReadonlyMcp.Tests;

public sealed class PublicToolErrorsTests
{
    [Theory]
    [InlineData("permission_denied", 229)]
    [InlineData("permission_denied", 916)]
    [InlineData("access_denied", null)]
    [InlineData("access_check_unavailable", 229)]
    public void PermissionErrorsAreUnifiedWithoutChangingInternalDiagnostics(string category, int? number)
    {
        var original = new ToolError(category, "PrivateDb.dbo.PrivateFunction requires EXECUTE for PrivateUser", number, 5, 14);
        var result = SqlServerTools.CreateToolResult(new { Error = original }, false, original.Message);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("access_denied", error.GetProperty("category").GetString());
        Assert.Equal("用户没有访问权限", error.GetProperty("message").GetString());
        Assert.Equal(error.GetProperty("message").GetString(), Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.DoesNotContain("Private", result.StructuredContent.Value.GetRawText());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("sqlErrorNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("sqlErrorState").ValueKind);
        Assert.Equal(JsonValueKind.Null, error.GetProperty("sqlErrorClass").ValueKind);
        Assert.Equal(category, original.Category);
        Assert.Equal(number, original.SqlErrorNumber);
        Assert.Contains("PrivateDb", original.Message);
    }

    [Theory]
    [InlineData(229)]
    [InlineData(53)]
    [InlineData(-2)]
    public void UnknownExecutionAlwaysTakesPrecedenceAndPreservesPartialData(int number)
    {
        var result = SqlServerTools.CreateToolResult(new
        {
            Error = new ToolError("execution_unknown", "Private dependency", number, 1, 14),
            ResultSets = new[] { new { Rows = new[] { new[] { 42 } } } },
            ReturnedRows = 1,
            Truncated = true,
            TruncationReason = "max_rows",
        }, false, "Private dependency");
        var body = result.StructuredContent!.Value;
        Assert.Equal("execution_unknown", body.GetProperty("error").GetProperty("category").GetString());
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("不得自动重试", text);
        Assert.Contains("部分结果", text);
        Assert.DoesNotContain("Private", text);
        Assert.True(result.IsError);
        Assert.Equal(42, body.GetProperty("resultSets")[0].GetProperty("rows")[0][0].GetInt32());
        Assert.Equal(1, body.GetProperty("returnedRows").GetInt32());
        Assert.True(body.GetProperty("truncated").GetBoolean());
        Assert.Equal("max_rows", body.GetProperty("truncationReason").GetString());
    }

    [Theory]
    [InlineData("invalid_input", "objectName 中的数据库与 database 参数不一致。")]
    [InlineData("safety_rejection", "不支持全局临时表。")]
    [InlineData("timeout", "请求超时。")]
    [InlineData("canceled", "操作已取消。")]
    [InlineData("busy", "当前繁忙。")]
    public void ActionableErrorsKeepTheirMeaning(string category, string message)
    {
        var result = PublicToolErrors.Present(new(category, message));
        Assert.Equal(category, result.Category);
        Assert.Equal(message, result.Message);
    }

    [Theory]
    [InlineData("internal_error", null, "服务暂时不可用。")]
    [InlineData("timeout", -2, "请求超时。")]
    [InlineData("sql_error", 50000, "查询执行失败。")]
    [InlineData("sql_error", 245, "数据类型转换失败，请检查输入值与目标类型。")]
    [InlineData("connection_error", 53, "连接失败，请确认网络连接后再试。")]
    [InlineData("authentication_or_database", 18456, "连接失败，请确认网络连接后再试。")]
    public void RawServerErrorsDoNotEscape(string category, int? number, string expected)
    {
        var result = PublicToolErrors.Present(new(category, "PrivateServer PrivateDb private-value", number));
        Assert.Equal(expected, result.Message);
        Assert.Equal(category, result.Category);
        Assert.Null(result.SqlErrorNumber);
    }
}
