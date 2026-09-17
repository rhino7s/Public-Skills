using Microsoft.Data.SqlClient;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Security;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class CatalogAccessIntegrationTests
{
    public static bool Configured => Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_CONFIG") is { Length: > 0 };
    public static bool ProcedureConfigured => Configured && Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_PROCEDURE") is { Length: > 0 };
    [Fact(Skip = "未配置过程入口核验案例。", SkipUnless = nameof(ProcedureConfigured))]
    public async Task ProcedureEntryChecksWithoutExecutingBusinessBody()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = SettingsLoader.Load(Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_CONFIG")!);
        var database = Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_DATABASE")!;
        var schema = Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_SCHEMA")!;
        var name = Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_PROCEDURE")!;
        var factory = new SqlConnectionFactory(settings);
        var store = new CapabilityStore(settings, factory, new QueryConcurrencyGate(settings));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        Assert.True(await store.CheckAsync(deadline.Token));
        Assert.True(await store.IsProcedureGrantedAsync(database,schema,name,deadline.Token));
        using var quote = new SqlCommandBuilder();
        var sql = "EXEC " + string.Join(".",new[] { database,schema,name }.Select(quote.QuoteIdentifier));
        Assert.True(new SqlSafetyAnalyzer().AnalyzeProcedureCall(sql,database,out var target).IsAllowed);
        await using var connection = await factory.OpenAsync(database,deadline.Token);
        var verification = await ProcedureTargetVerifier.VerifyAsync(connection,target!,15,deadline.Token,database);
        Assert.Null(verification.Error);
        // 不提交 sql；只调用产品内部的固定元数据核验。
    }

    [Fact(Skip = "未配置目录对象核验案例。", SkipUnless = nameof(Configured))]
    public async Task AuthorizedFunctionHasSupportedTypeAndPermissions()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = SettingsLoader.Load(Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_CONFIG")!);
        var database = Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_DATABASE")!;
        var schema = Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_SCHEMA")!;
        var name = Environment.GetEnvironmentVariable("MCP_CATALOG_TEST_FUNCTION")!;
        var factory = new SqlConnectionFactory(settings);
        var gate = new QueryConcurrencyGate(settings);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var service = new CatalogAccessService(settings, factory, gate);
        var error = await service.VerifyAsync([new CatalogObject(database,schema,name,true)], deadline.Token);
        if (error is null) return;
        // 固定用途诊断，仅输出类型/权限，不读取定义、业务行或真实身份。
        await using var connection = await factory.OpenAsync(database, deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT o.type, o.is_ms_shipped, HAS_PERMS_BY_NAME(QUOTENAME(s.name)+N'.'+QUOTENAME(o.name),N'OBJECT',N'SELECT'), HAS_PERMS_BY_NAME(QUOTENAME(s.name)+N'.'+QUOTENAME(o.name),N'OBJECT',N'EXECUTE') FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id WHERE s.name=@s COLLATE CATALOG_DEFAULT AND o.name=@n COLLATE CATALOG_DEFAULT;";
        command.Parameters.AddWithValue("@s",schema);
        command.Parameters.AddWithValue("@n",name);
        await using var reader = await command.ExecuteReaderAsync(deadline.Token);
        var diagnostics = new List<string>();
        while (await reader.ReadAsync(deadline.Token)) diagnostics.Add($"type={reader[0]},system={reader[1]},select={reader[2]},execute={reader[3]}");
        await reader.DisposeAsync();
        command.CommandText = "SELECT HAS_PERMS_BY_NAME(QUOTENAME(@s)+N'.'+QUOTENAME(@n),N'OBJECT',N'SELECT');";
        diagnostics.Add("direct_select_permission=" + Convert.ToString(await command.ExecuteScalarAsync(deadline.Token)));
        Assert.Fail(error.Category+": "+string.Join(";",diagnostics));
    }
}
