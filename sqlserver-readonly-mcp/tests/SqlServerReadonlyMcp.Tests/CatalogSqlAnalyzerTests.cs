using SqlServerReadonlyMcp.Security;

namespace SqlServerReadonlyMcp.Tests;

public sealed class CatalogSqlAnalyzerTests
{
    private static CatalogAnalysis Analyze(string sql) => new CatalogSqlAnalyzer().Analyze(sql, token: TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("SELECT SUM(x) OVER(PARTITION BY m ORDER BY d ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW), RANK() OVER(ORDER BY x DESC) FROM D.dbo.T")]
    [InlineData("SELECT ROW_NUMBER() OVER(ORDER BY x), DENSE_RANK() OVER(ORDER BY x), LAG(x) OVER(ORDER BY x), LEAD(x) OVER(ORDER BY x), FIRST_VALUE(x) OVER(ORDER BY x), LAST_VALUE(x) OVER(ORDER BY x) FROM D.dbo.T")]
    [InlineData("WITH c AS (SELECT * FROM D.dbo.T) SELECT * FROM c a JOIN c b ON a.id=b.id")]
    [InlineData("SELECT * FROM (SELECT * FROM D.dbo.T) x WHERE EXISTS(SELECT 1 FROM D.dbo.T WHERE x.id=id)")]
    [InlineData("SELECT CONVERT(decimal(10,2),COALESCE(x,0)), CAST(x AS int), TRY_CONVERT(int,x), CASE WHEN x>0 THEN x ELSE NULL END FROM D.dbo.T")]
    [InlineData("SELECT * INTO #x FROM D.dbo.T; SELECT * FROM #x; DROP TABLE #x;")]
    [InlineData("CREATE TABLE #x(id int); INSERT #x SELECT id FROM D.dbo.T; UPDATE #x SET id=1; SELECT * FROM #x")]
    [InlineData("DECLARE @x TABLE(id int); INSERT @x SELECT id FROM D.dbo.T; SELECT * FROM @x")]
    public void CommonQueriesCollectOnlyActualPersistentSource(string sql)
    {
        var result = Analyze(sql);
        Assert.True(result.Safety.IsAllowed, result.Safety.Message);
        Assert.Equal(new CatalogObject("D", "dbo", "T", false), Assert.Single(result.Objects));
    }

    [Fact]
    public void AllExpressionsAndStatementsAreCollected()
    {
        var result = Analyze("SELECT D.dbo.scalarFn(x) FROM D.dbo.T OUTER APPLY E.dbo.tvf(1) q WHERE D.dbo.predicateFn(x)=1; SELECT * FROM F.dbo.Other");
        Assert.True(result.Safety.IsAllowed, result.Safety.Message);
        Assert.Equal(5, result.Objects.Count);
        Assert.Contains(new("D", "dbo", "scalarFn", true), result.Objects);
        Assert.Contains(new("E", "dbo", "tvf", true), result.Objects);
        Assert.Contains(new("F", "dbo", "Other", false), result.Objects);
    }

    [Theory]
    [InlineData("SELECT * FROM T")]
    [InlineData("SELECT * FROM dbo.T")]
    [InlineData("SELECT * FROM D..T")]
    [InlineData("SELECT * FROM S.D.dbo.T")]
    [InlineData("SELECT * FROM D.sys.tables")]
    [InlineData("SELECT * FROM D.INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT OBJECT_DEFINITION(1)")]
    [InlineData("SELECT {fn DATABASE()}")]
    [InlineData("SELECT {fn USER()}")]
    [InlineData("SELECT @@VERSION")]
    [InlineData("SELECT $PARTITION.pf(1)")]
    [InlineData("CREATE TABLE #x(id int REFERENCES D.dbo.T(id))")]
    [InlineData("DECLARE @x xml(dbo.Collection)")]
    [InlineData("SELECT CURRENT_USER")]
    [InlineData("SELECT * FROM OPENQUERY(x,'select 1')")]
    [InlineData("SELECT * FROM OPENJSON('[]')")]
    [InlineData("SELECT * FROM D.dbo.T OPTION(QUERYTRACEON 1)")]
    [InlineData("SELECT * FROM D.dbo.T WITH(NOLOCK)")]
    [InlineData("SELECT * FROM tempdb.dbo.#x")]
    [InlineData("SELECT * FROM #notCreated")]
    [InlineData("WITH x AS (SELECT * FROM D.dbo.T) SELECT * FROM x; SELECT * FROM x;")]
    [InlineData("DECLARE @x dbo.MyType;")]
    [InlineData("WAITFOR DELAY '00:00:01'")]
    [InlineData("SELECT 1; EXEC D.dbo.p;")]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    public void UnsupportedOrAmbiguousAccessIsRejected(string sql) => Assert.False(Analyze(sql).Safety.IsAllowed);

    [Theory]
    [InlineData("SELECT SUM(x) OVER(ORDER BY E.dbo.f(x)) FROM D.dbo.T")]
    [InlineData("SELECT * FROM D.dbo.T ORDER BY E.dbo.f(x)")]
    [InlineData("SELECT * FROM D.dbo.T WHERE EXISTS(SELECT 1 FROM E.dbo.f(1))")]
    [InlineData("SELECT * INTO #x FROM D.dbo.T; INSERT #x SELECT * FROM E.dbo.f(1)")]
    [InlineData("WITH c AS (SELECT * FROM E.dbo.f(1)) SELECT * FROM c JOIN D.dbo.T t ON 1=1")]
    public void HiddenFunctionReferencesAreNeverOmitted(string sql)
    {
        var result=Analyze(sql);
        Assert.True(result.Safety.IsAllowed,result.Safety.Message);
        Assert.Contains(new("E","dbo","f",true),result.Objects);
        Assert.Contains(new("D","dbo","T",false),result.Objects);
    }

    [Fact]
    public void NamesPreserveCaseAndEscaping()
    {
        var result = Analyze("SELECT * FROM [D].[dbo].[a]]b] UNION ALL SELECT * FROM [D].[dbo].[A]]b]");
        Assert.True(result.Safety.IsAllowed, result.Safety.Message);
        Assert.Equal(2, result.Objects.Count);
        Assert.Contains(new("D", "dbo", "a]b", false), result.Objects);
    }

    [Fact]
    public void RepeatedReferencesDoNotConsumeObjectBudget()
    {
        var result = Analyze(string.Join(";", Enumerable.Repeat("SELECT * FROM D.dbo.T", 130)));
        Assert.True(result.Safety.IsAllowed, result.Safety.Message);
        Assert.Single(result.Objects);
        Assert.False(Analyze(string.Join(";", Enumerable.Range(0, 129).Select(i => $"SELECT * FROM D.dbo.T{i}"))).Safety.IsAllowed);
    }

    [Theory]
    [InlineData("EXEC D.dbo.p", true)]
    [InlineData("EXEC dbo.p", false)]
    [InlineData("EXEC p", false)]
    public void ProcedureRequiresThreeParts(string sql, bool allowed) => Assert.Equal(allowed, new CatalogSqlAnalyzer().Analyze(sql, true, TestContext.Current.CancellationToken).Safety.IsAllowed);
}
