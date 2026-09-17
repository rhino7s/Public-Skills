using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class CatalogAccessQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaximumBatchHasParameterNamesAndValidSql(bool metadata)
    {
        var settings = new McpSettings { Capabilities = new() { ListFunction = "D.dbo.list" } };
        var service = new CatalogAccessService(settings, new SqlConnectionFactory(settings),new QueryConcurrencyGate(settings));
        var sql=service.BuildQuery(128,metadata);
        new TSql180Parser(true).Parse(new StringReader(sql),out var errors);
        Assert.Empty(errors);
        Assert.Contains("@s127",sql);
        Assert.Contains("@n127",sql);
        Assert.Contains("@f127",sql);
        Assert.Contains("COLLATE CATALOG_DEFAULT",sql);
        Assert.DoesNotContain("desp",sql,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET",sql,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TOP",sql,StringComparison.OrdinalIgnoreCase);
        if (!metadata) Assert.Contains("DB_ID(PARSENAME(c.iname,3)) = DB_ID()",sql);
    }
}
