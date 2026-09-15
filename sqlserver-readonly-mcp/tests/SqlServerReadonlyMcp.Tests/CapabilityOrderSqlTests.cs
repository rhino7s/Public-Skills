using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlServerReadonlyMcp.Tests;

public sealed class CapabilityOrderSqlTests
{
    [Theory]
    [InlineData("create-capabilities-functions.sql")]
    [InlineData("create-capabilities-tables.sql")]
    [InlineData("migrate-capability-grant-order.sql")]
    [InlineData("migrate-capability-role-active.sql")]
    public void DeploymentScriptParses(string filename) => Parse(filename);

    [Fact]
    public void ReadOnlyFixtureUsesActualFunctionQuery()
    {
        var sql = BuildFixture();
        new TSql180Parser(true).Parse(new StringReader(sql), out var errors);
        Assert.Empty(errors);
    }

    internal static string BuildFixture()
    {
        var (source, fragment) = Parse("create-capabilities-functions.sql");
        var visitor = new DirectoryQueryVisitor();
        fragment.Accept(visitor);
        var statement = Assert.Single(visitor.Queries);
        // 测试实际部署脚本中的查询，只将来源替换为表变量，不复制排序算法。
        var query = source.Substring(statement.StartOffset, statement.FragmentLength)
            .Replace("dbo.tools_grant", "@grants", StringComparison.Ordinal)
            .Replace("dbo.tools_role_group", "@roles", StringComparison.Ordinal)
            .Replace("dbo.tools_info", "@info", StringComparison.Ordinal).TrimEnd().TrimEnd(';');
        return """
            DECLARE @u_name nvarchar(128) = N'test';
            DECLARE @grants TABLE (gid int, u_name nvarchar(128), rname varchar(50), ord int, active bit);
            DECLARE @roles TABLE (rname varchar(50), tool_id int, ord int, active bit NOT NULL DEFAULT (1));
            DECLARE @info TABLE (id int, itype varchar(5), iname varchar(150), summary nvarchar(1000), desp nvarchar(max), active bit);
            INSERT @grants VALUES (1,N'test','A',1,1),(2,N'test','B',2,1),
                (3,N'test','C',1,1),(4,N'test','disabled',-10,0),(5,N'other','other',-20,1);
            INSERT @roles (rname, tool_id, ord) VALUES ('A',1,30),('A',2,40),('B',1,1),('B',3,1),
                ('C',4,20),('C',5,20),('disabled',6,-1),('other',6,-2),('A',7,-3),('A',8,-4);
            INSERT @info VALUES (1,'grant','db.dbo.x',N'X',N'',1),
                (2,'skill','',N'Y',N'detail',1),(3,'grant','db.dbo.z',N'Z',N'',1),
                (4,'grant','db.dbo.w',N'W',N'',1),(5,'grant','db.dbo.v',N'V',N'',1),
                (6,'skill','',N'No grant',N'',1),(7,'skill','',N'Inactive',N'',0),
                (8,'other','',N'Invalid kind',N'',1);
            """ + "\n" + query + " ORDER BY ord, id;\n"
            + query + " ORDER BY ord, id OFFSET 2 ROWS FETCH NEXT 2 ROWS ONLY;\n"
            + "UPDATE @grants SET ord = 0;\n" + query + " ORDER BY ord, id;\n"
            + "UPDATE @roles SET active = 0 WHERE rname = 'B' OR tool_id = 4;\n"
            + query + " ORDER BY ord, id;\n"
            + "UPDATE @roles SET active = 1;\n" + query + " ORDER BY ord, id;\n"
            + "UPDATE @roles SET active = 0;\n" + query + " ORDER BY ord, id;";
    }

    private static (string Source, TSqlFragment Fragment) Parse(string filename)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, filename));
        var fragment = new TSql180Parser(true).Parse(new StringReader(source), out var errors);
        Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Line}:{e.Column} {e.Message}")));
        return (source, fragment);
    }

    private sealed class DirectoryQueryVisitor : TSqlFragmentVisitor
    {
        public List<SelectStatement> Queries { get; } = [];
        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.WithCtesAndXmlNamespaces?.CommonTableExpressions.Any(c => c.ExpressionName.Value == "assigned_tools") == true)
                Queries.Add(node);
            base.ExplicitVisit(node);
        }
    }
}
