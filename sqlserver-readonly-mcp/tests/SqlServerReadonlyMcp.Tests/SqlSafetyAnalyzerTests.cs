using SqlServerReadonlyMcp.Security;

namespace SqlServerReadonlyMcp.Tests;

public sealed class SqlSafetyAnalyzerTests
{
    private readonly SqlSafetyAnalyzer _analyzer = new();

    [Fact]
    public void AllowsComplexReadBatchAndLocalTempTable()
    {
        const string sql = """
            DECLARE @minimumObjectId int = 0;
            SELECT TOP (10) o.object_id, o.name
            INTO #objects
            FROM ExampleDatabase.sys.objects AS o
            WHERE o.object_id > @minimumObjectId
            ORDER BY o.object_id;

            WITH ranked AS
            (
                SELECT object_id, name, ROW_NUMBER() OVER (ORDER BY object_id) AS rn
                FROM #objects
            )
            SELECT * FROM ranked WHERE rn <= 5;
            """;

        var result = _analyzer.Analyze(sql);

        Assert.True(result.IsAllowed, result.Message);
    }

    [Fact]
    public void AllowsWritesOnlyToLocalTemporaryTablesAndTableVariables()
    {
        const string sql = """
            CREATE TABLE #work (id int NOT NULL, value int NULL);
            INSERT INTO #work (id, value) VALUES (1, 10);
            UPDATE #work SET value = 20 WHERE id = 1;
            DELETE FROM #work WHERE id = 2;
            TRUNCATE TABLE #work;
            ALTER TABLE #work ADD note nvarchar(20) NULL;
            DROP TABLE #work;

            DECLARE @items TABLE (id int NOT NULL, value int NULL);
            INSERT INTO @items (id, value) VALUES (1, 10);
            UPDATE @items SET value = 20 WHERE id = 1;
            DELETE FROM @items WHERE id = 2;
            SELECT id, value FROM @items;
            """;

        var result = _analyzer.Analyze(sql);

        Assert.True(result.IsAllowed, result.Message);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.ExampleTable (id) VALUES (1);")]
    [InlineData("UPDATE dbo.ExampleTable SET value = 1 WHERE id = 1;")]
    [InlineData("DELETE FROM dbo.ExampleTable WHERE id = 1;")]
    [InlineData("MERGE dbo.ExampleTable AS target USING #source AS source ON target.id = source.id WHEN MATCHED THEN UPDATE SET value = source.value;")]
    [InlineData("SELECT object_id INTO dbo.PersistentCopy FROM sys.objects;")]
    [InlineData("CREATE TABLE dbo.PersistentTable (id int);")]
    [InlineData("ALTER TABLE dbo.ExampleTable ADD value int NULL;")]
    [InlineData("DROP TABLE dbo.ExampleTable;")]
    [InlineData("TRUNCATE TABLE dbo.ExampleTable;")]
    [InlineData("CREATE VIEW dbo.ExampleView AS SELECT 1 AS value;")]
    [InlineData("GRANT SELECT ON dbo.ExampleTable TO public;")]
    [InlineData("BULK INSERT dbo.ExampleTable FROM 'C:\\example.csv';")]
    [InlineData("UPDATE #work SET value = 1 OUTPUT inserted.id INTO dbo.PersistentAudit(id);")]
    [InlineData("DBCC CHECKIDENT ('dbo.ExampleTable', RESEED, 0);")]
    [InlineData("UPDATE STATISTICS dbo.ExampleTable;")]
    [InlineData("ENABLE TRIGGER ALL ON dbo.ExampleTable;")]
    [InlineData("CHECKPOINT;")]
    [InlineData("SETUSER 'dbo';")]
    public void RejectsPersistentWritesAndDefinitions(string sql)
    {
        var result = _analyzer.Analyze(sql);

        Assert.False(result.IsAllowed);
        Assert.Equal("persistent_write_not_allowed", result.Code);
    }

    [Fact]
    public void RejectsDatabaseSwitch()
    {
        var result = _analyzer.Analyze("USE ExampleDatabase; SELECT 1;");

        Assert.False(result.IsAllowed);
        Assert.Equal("database_switch_not_allowed", result.Code);
    }

    [Theory]
    [InlineData("EXEC dbo.some_procedure @id = 1;")]
    [InlineData("EXECUTE(N'SELECT 1');")]
    [InlineData("DECLARE @sql nvarchar(max) = N'SELECT 1'; EXEC sp_executesql @sql;")]
    [InlineData("EXECUTE AS USER = 'dbo'; SELECT 1; REVERT;")]
    [InlineData("CREATE TABLE #result (id int); INSERT INTO #result EXEC dbo.DangerousProcedure;")]
    [InlineData("CREATE TABLE #result (id int); INSERT INTO #result EXEC(N'DELETE FROM dbo.PersistentTable; SELECT 1;');")]
    [InlineData("DECLARE @result TABLE (id int); INSERT INTO @result EXEC OtherDatabase.dbo.DangerousProcedure;")]
    public void RejectsExecute(string sql)
    {
        var result = _analyzer.Analyze(sql);

        Assert.False(result.IsAllowed);
        Assert.Equal("execute_not_allowed", result.Code);
    }

    [Theory]
    [InlineData("CREATE TABLE #result (id int); INSERT INTO #result(id) SELECT id FROM (DELETE FROM dbo.PersistentTable OUTPUT DELETED.id) AS deleted_rows;")]
    [InlineData("CREATE TABLE #result (id int); INSERT INTO #result(id) SELECT id FROM (UPDATE dbo.PersistentTable SET value = 1 OUTPUT INSERTED.id) AS changed_rows;")]
    [InlineData("CREATE TABLE #result (id int); INSERT INTO #result(id) SELECT id FROM (INSERT INTO dbo.PersistentTable(id) OUTPUT INSERTED.id VALUES (1)) AS inserted_rows;")]
    [InlineData("CREATE TABLE #source (id int); CREATE TABLE #result (id int); INSERT INTO #result(id) SELECT id FROM (MERGE dbo.PersistentTable AS target USING #source AS source ON target.id = source.id WHEN NOT MATCHED THEN INSERT (id) VALUES (source.id) OUTPUT INSERTED.id) AS merged_rows;")]
    [InlineData("CREATE TABLE #work (id int); CREATE TABLE #result (id int); INSERT INTO #result(id) SELECT id FROM (DELETE FROM #work OUTPUT DELETED.id) AS deleted_rows;")]
    public void RejectsNestedDataModificationSources(string sql)
    {
        var result = _analyzer.Analyze(sql);

        Assert.False(result.IsAllowed);
        Assert.Equal("nested_dml_not_allowed", result.Code);
    }

    [Theory]
    [InlineData("SELECT * FROM [LinkedServer].[Database].[dbo].[TableName];")]
    [InlineData("SELECT * FROM [LinkedServer].[Database].[dbo].[TableFunction]();")]
    [InlineData("SELECT * FROM OPENQUERY(LinkedServer, 'SELECT * FROM dbo.TableName');")]
    [InlineData("SELECT * FROM OPENDATASOURCE('MSOLEDBSQL', 'Data Source=server;User ID=user;Password=password').[Database].[dbo].[TableName];")]
    [InlineData("SELECT * FROM OPENROWSET('MSOLEDBSQL', 'Server=server;Trusted_Connection=yes;', 'SELECT 1') AS source;")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'C:\\sensitive.txt', SINGLE_CLOB) AS source;")]
    public void RejectsExplicitRemoteAndAdHocDataSources(string sql)
    {
        var result = _analyzer.Analyze(sql);

        Assert.False(result.IsAllowed);
        Assert.Equal("external_data_source_not_allowed", result.Code);
    }

    [Theory]
    [InlineData("SELECT NEXT VALUE FOR dbo.OrderSequence;")]
    [InlineData("DECLARE @value bigint = NEXT VALUE FOR dbo.OrderSequence; SELECT @value;")]
    public void RejectsSequenceValueGeneration(string sql)
    {
        var result = _analyzer.Analyze(sql);

        Assert.False(result.IsAllowed);
        Assert.Equal("sequence_mutation_not_allowed", result.Code);
    }

    [Theory]
    [InlineData("SELECT * INTO ##global_result FROM sys.objects;")]
    [InlineData("CREATE TABLE [##global_result] (id int);")]
    public void RejectsGlobalTemporaryTable(string sql)
    {
        var result = _analyzer.Analyze(sql);

        Assert.False(result.IsAllowed);
        Assert.Equal("global_temp_table_not_allowed", result.Code);
    }

    [Fact]
    public void DoesNotTreatStringsOrCommentsAsGlobalTemporaryTables()
    {
        const string sql = """
            -- 文档示例：##not_a_table
            SELECT N'##also_not_a_table' AS sample, '#local' AS local_name;
            """;

        var result = _analyzer.Analyze(sql);

        Assert.True(result.IsAllowed, result.Message);
    }

    [Fact]
    public void RejectsInvalidSqlBeforeConnection()
    {
        var result = _analyzer.Analyze("SELECT FROM WHERE;");

        Assert.False(result.IsAllowed);
        Assert.Equal("parse_error", result.Code);
    }

    [Theory]
    [InlineData("EXEC ExampleProcedure;")]
    [InlineData("EXEC dbo.ExampleProcedure;")]
    [InlineData("EXEC dbo.ExampleProcedure 'a', 1;")]
    [InlineData("EXEC ExampleDatabase.dbo.ExampleProcedure @code = 'a', @count = 1;")]
    public void AllowsDirectStaticProcedureCall(string sql)
    {
        var result = _analyzer.AnalyzeProcedureCall(sql, "ExampleDatabase");

        Assert.True(result.IsAllowed, result.Message);
    }

    [Theory]
    [InlineData("EXEC @procedureName;", "variable_procedure_not_allowed")]
    [InlineData("EXEC(N'SELECT 1');", "dynamic_execute_not_allowed")]
    [InlineData("EXEC sp_executesql N'SELECT 1';", "sp_executesql_not_allowed")]
    [InlineData("EXEC sys.sp_executesql N'SELECT 1';", "sp_executesql_not_allowed")]
    [InlineData("EXEC LinkedServer.ExampleDatabase.dbo.ExampleProcedure;", "linked_server_procedure_not_allowed")]
    [InlineData("EXEC dbo.FirstProcedure; EXEC dbo.SecondProcedure;", "single_procedure_call_required")]
    [InlineData("EXEC dbo.ExampleProcedure; SELECT 1;", "single_procedure_call_required")]
    [InlineData("EXECUTE AS USER = 'dbo';", "single_procedure_call_required")]
    public void RejectsUnsafeProcedureCall(string sql, string expectedCode)
    {
        var result = _analyzer.AnalyzeProcedureCall(sql, "ExampleDatabase");

        Assert.False(result.IsAllowed);
        Assert.Equal(expectedCode, result.Code);
    }

    [Theory]
    [InlineData("EXEC OtherDatabase.dbo.ExampleProcedure;")]
    [InlineData("EXEC [OtherDatabase]..[ExampleProcedure];")]
    public void RejectsProcedureDatabaseDifferentFromToolDatabase(string sql)
    {
        var result = _analyzer.AnalyzeProcedureCall(sql, "ExampleDatabase");

        Assert.False(result.IsAllowed);
        Assert.Equal("procedure_database_mismatch", result.Code);
    }

    [Theory]
    [InlineData("EXEC ExampleDatabase.dbo.ExampleProcedure;")]
    [InlineData("EXEC [exampledatabase]..[ExampleProcedure];")]
    public void AllowsProcedureDatabaseMatchingToolDatabase(string sql)
    {
        var result = _analyzer.AnalyzeProcedureCall(sql, "ExampleDatabase");

        Assert.True(result.IsAllowed, result.Message);
    }
}
