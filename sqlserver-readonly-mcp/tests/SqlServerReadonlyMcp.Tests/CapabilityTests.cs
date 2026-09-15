using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tests;

public sealed class CapabilityTests
{
    [Fact]
    public void CheckRequiresRealNonNullBit()
    {
        Assert.True(CapabilityStore.ReadCheckValue(true));
        Assert.False(CapabilityStore.ReadCheckValue(false));
        foreach (var value in new object?[] { null, DBNull.Value, 1, 0, "true", "1" })
            Assert.Throws<InvalidDataException>(() => CapabilityStore.ReadCheckValue(value));
    }

    [Theory]
    [InlineData("Db.dbo.fn", "[Db].[dbo].[fn]")]
    [InlineData("[Db.With.Dot].[dbo].[fn]]name]", "[Db.With.Dot].[dbo].[fn]]name]")]
    public void QuotesConfiguredIdentifiers(string value, string sql) => Assert.Equal(sql, CapabilityFunctionName.Parse(value).Sql);

    [Theory]
    [InlineData("Db.dbo.fn(); DROP TABLE dbo.x")]
    [InlineData("server.Db.dbo.fn")]
    [InlineData("Db.other.fn")]
    [InlineData("Db..fn")]
    [InlineData("Db.dbo.fn --comment")]
    [InlineData("Db.dbo.[x\ny]")]
    public void RejectsSqlFragments(string value) => Assert.Throws<ArgumentException>(() => CapabilityFunctionName.Parse(value));

    [Fact]
    public void ValidatesAdPairAndPositivePageSize()
    {
        Assert.Throws<SettingsException>(() => SettingsValidator.Validate(Settings(check: "")));
        Assert.Throws<SettingsException>(() => SettingsValidator.Validate(Settings(pageSize: 0)));
        Assert.Throws<SettingsException>(() => SettingsValidator.Validate(Settings(enabled: false)));
        SettingsValidator.Validate(Settings());
        SettingsValidator.Validate(Settings(ad: false, check: ""));
    }

    [Fact]
    public async Task RevocationAndRecoveryAreNotCached()
    {
        var store = new FakeStore { Allowed = true };
        var service = Service(Settings(), store);
        Assert.Null(await service.CheckAccessAsync(CancellationToken.None));
        store.Allowed = false;
        Assert.Equal("access_denied", (await service.CheckAccessAsync(CancellationToken.None))!.StructuredContent!.Value.GetProperty("code").GetString());
        store.Allowed = true;
        Assert.Null(await service.CheckAccessAsync(CancellationToken.None));
        Assert.Equal(3, store.Checks);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SqlOrDisabledCatalogSkipsCheck(bool ad, bool enabled)
    {
        var store = new FakeStore { CheckFailure = new UnauthorizedAccessException() };
        var service = Service(Settings(ad: ad, enabled: enabled), store);
        Assert.Null(await service.CheckAccessAsync(CancellationToken.None));
        Assert.Equal(0, store.Checks);
    }

    [Fact]
    public async Task CheckFailuresStayBlockedButDistinguishTheirCause()
    {
        foreach (var (error, code) in new (Exception, string)[] {
            (new UnauthorizedAccessException("secret database"), "access_denied"),
            (new InvalidDataException("secret function"), "access_check_unavailable"),
            (new TimeoutException("secret server"), "access_check_unavailable"),
            (new OperationCanceledException(), "access_check_unavailable") })
        {
            var store = new FakeStore { CheckFailure = error };
            var result = await Service(Settings(), store).CheckAccessAsync(CancellationToken.None);
            Assert.NotNull(result);
            Assert.True(result.IsError);
            Assert.Equal(code, result.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.DoesNotContain("secret", Text(result));
            Assert.Equal(0, store.Reads);
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotPermissionDenial()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var result = await Service(Settings(), new FakeStore()).CheckAccessAsync(source.Token);
        Assert.Equal("canceled", result!.StructuredContent!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PagesSummariesWithoutDescriptionsOrDuplicatingText()
    {
        var store = new FakeStore { Rows = [new(7, -5, "rule", ObjectName: "", HasDescription: true), new(2, 99, "business", ObjectName: "Db.dbo.p"), new(3, 99, "last")] };
        var service = Service(Settings(pageSize: 2), store);
        var first = await service.ListAsync(0, CancellationToken.None);
        var content = first.StructuredContent!.Value;
        Assert.Equal(2, content.GetProperty("items").GetArrayLength());
        Assert.Equal(7, content.GetProperty("items")[0].GetProperty("id").GetInt32());
        Assert.True(content.GetProperty("items")[0].GetProperty("has_desp").GetBoolean());
        Assert.False(content.GetProperty("items")[0].TryGetProperty("desp", out _));
        Assert.DoesNotContain("business", Text(first));
        Assert.Equal(2, content.GetProperty("next_offset").GetInt64());
        var second = await service.ListAsync(2, CancellationToken.None);
        Assert.Equal("last", second.StructuredContent!.Value.GetProperty("items")[0].GetProperty("summary").GetString());
        Assert.False(second.StructuredContent.Value.GetProperty("has_more").GetBoolean());
        Assert.Equal((2L, 3), store.LastRequest);
    }

    [Fact]
    public async Task SizeLimitStopsAtWholeSummaryAndNeverFallsBackToDetails()
    {
        var store = new FakeStore { Rows = [new(1, 0, "first"), new(2, 0, new string('中', 20_000), Oversized: true)] };
        var service = Service(Settings(), store);
        var first = await service.ListAsync(0, CancellationToken.None);
        Assert.Equal(1, first.StructuredContent!.Value.GetProperty("next_offset").GetInt64());
        var second = await service.ListAsync(1, CancellationToken.None);
        Assert.True(second.IsError);
        Assert.Equal("capability_too_large", second.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first, Program.CreateToolJsonOptions()).Length < 16 * 1024);
        Assert.Equal(0, store.DetailReads);
    }

    [Fact]
    public async Task DetailsPreserveMarkdownAndTrustDatabaseHasDescriptionFlag()
    {
        var store = new FakeStore { Detail = new(1, "Db.dbo.p", "summary", true, "### 参数\n\n```sql\nSELECT 1;\n```") };
        var service = Service(Settings(), store);
        var first = await service.DetailsAsync(1, CancellationToken.None);
        Assert.Equal(store.Detail.Description, first.StructuredContent!.Value.GetProperty("desp").GetString());
        Assert.DoesNotContain("SELECT", Text(first));
        store.Detail = new(1, "", "summary", true, "\r\n\t");
        Assert.NotEqual(true, (await service.DetailsAsync(1, CancellationToken.None)).IsError);
        store.Detail = new(1, "", "summary", false, "   ");
        var noDetails = await service.DetailsAsync(1, CancellationToken.None);
        Assert.False(noDetails.StructuredContent!.Value.GetProperty("has_desp").GetBoolean());
        Assert.Equal("   ", noDetails.StructuredContent.Value.GetProperty("desp").GetString());
        store.Detail = null;
        Assert.Equal("access_denied", (await service.DetailsAsync(1, CancellationToken.None)).StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Equal(4, store.DetailReads);
    }

    [Fact]
    public async Task InvalidDetailsAndOversizeFailWithoutPartialBody()
    {
        foreach (var row in new CapabilityDetail[] {
            new(2, "", "summary", true, "private"), new(1, "", " ", true, "private"),
            new(1, "", "summary", true, ""), new(1, "", "summary", true, null!) })
        {
            var result = await Service(Settings(), new FakeStore { Detail = row }).DetailsAsync(1, CancellationToken.None);
            Assert.Equal("capabilities_unavailable", result.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.DoesNotContain("private", Text(result));
        }
        var oversized = await Service(Settings(), new FakeStore { Detail = new(1, "", "summary", true, new string('a', 20_000)) }).DetailsAsync(1, CancellationToken.None);
        Assert.Equal("capability_too_large", oversized.StructuredContent!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidIdAndDisabledCatalogDoNotReadDetails()
    {
        var store = new FakeStore();
        Assert.True((await Service(Settings(), store).DetailsAsync(0, CancellationToken.None)).IsError);
        Assert.True((await Service(Settings(enabled: false), store).DetailsAsync(1, CancellationToken.None)).IsError);
        Assert.Equal(0, store.DetailReads);
    }

    [Fact]
    public async Task EmptyFirstPageDeniesAdButOffsetBeyondEndDoesNot()
    {
        var service = Service(Settings(), new FakeStore());
        Assert.True((await service.ListAsync(0, CancellationToken.None)).IsError);
        Assert.NotEqual(true, (await service.ListAsync(100, CancellationToken.None)).IsError);
        Assert.NotEqual(true, (await Service(Settings(ad: false), new FakeStore()).ListAsync(0, CancellationToken.None)).IsError);
    }

    [Fact]
    public async Task InvalidPageFailsAtomically()
    {
        foreach (var rows in new CapabilityRow[][] {
            [new(1, 0, "first"), new(1, 1, "duplicate")],
            [new(1, 2, "first"), new(2, 1, "out of order")],
            [new(1, 0, "first"), new(2, 1, " ")] })
        {
            var result = await Service(Settings(), new FakeStore { Rows = rows }).ListAsync(0, CancellationToken.None);
            Assert.True(result.IsError);
            Assert.Equal("capabilities_unavailable", result.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.DoesNotContain("first", Text(result));
        }
    }

    [Fact]
    public async Task InvalidOffsetsDoNotQuery()
    {
        var store = new FakeStore();
        var service = Service(Settings(), store);
        Assert.True((await service.ListAsync(-1, CancellationToken.None)).IsError);
        Assert.True((await service.ListAsync(long.MaxValue, CancellationToken.None)).IsError);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcedureGrantIsMandatoryForEveryAccountAndNeverCached(bool ad)
    {
        var store = new FakeStore { Allowed = false };
        var service = Service(Settings(ad: ad), store);
        Assert.Equal("access_denied", (await service.CheckProcedureAccessAsync("Db", "dbo", "sp_test", CancellationToken.None))?.Category);
        store.Allowed = true;
        Assert.Null(await service.CheckProcedureAccessAsync("Db", "dbo", "sp_test", CancellationToken.None));
        store.Allowed = false;
        Assert.Equal("access_denied", (await service.CheckProcedureAccessAsync("Db", "dbo", "sp_test", CancellationToken.None))?.Category);
        Assert.Equal(3, store.Checks);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task DisabledCatalogDeniesProcedureWithoutQuerying()
    {
        var store = new FakeStore { Allowed = true };
        Assert.Equal("access_denied", (await Service(Settings(ad: false, enabled: false), store)
            .CheckProcedureAccessAsync("Db", "dbo", "sp_test", CancellationToken.None))?.Category);
        Assert.Equal(0, store.Checks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcedureGrantFailureCannotFallBackToDatabasePermission(bool ad)
    {
        var service = Service(Settings(ad: ad), new FakeStore { CheckFailure = new InvalidDataException("private") });
        var error = await service.CheckProcedureAccessAsync("Db", "dbo", "sp_test", CancellationToken.None);
        Assert.Equal("access_check_unavailable", error?.Category);
        Assert.DoesNotContain("private", error!.Message);
    }

    [Fact]
    public async Task DetailsFailureAndCancellationDoNotLeakDiagnostics()
    {
        foreach (var (exception, code) in new (Exception, string)[] {
            (new InvalidDataException("private duplicate id or invalid type"), "capabilities_unavailable"),
            (new TimeoutException("private server"), "capabilities_unavailable"),
            (new UnauthorizedAccessException("private object"), "access_denied") })
        {
            var result = await Service(Settings(), new FakeStore { DetailFailure = exception }).DetailsAsync(1, CancellationToken.None);
            Assert.Equal(code, result.StructuredContent!.Value.GetProperty("code").GetString());
            Assert.DoesNotContain("private", Text(result));
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var store = new FakeStore();
        Assert.Equal("canceled", (await Service(Settings(), store).DetailsAsync(1, canceled.Token)).StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Equal(0, store.DetailReads);
    }

    [Fact]
    public void SummaryProjectionDoesNotRequestBody()
    {
        Assert.Equal(new[] { "id", "ord", "iname", "summary", "has_desp" }, CapabilityStore.SummaryProjection.Split(", "));
    }

    [Fact]
    public async Task ManyUnrelatedBodiesStayOutOfTaskResponses()
    {
        // Synthetic CB inventory + market scenario: one mandatory rule, two relevant bodies, many unrelated bodies.
        var rows = Enumerable.Range(1, 40).Select(id => new CapabilityRow(id, id, id <= 3 ? "适用规则或 CB 库存行情" : "其他业务", HasDescription: true)).ToArray();
        var store = new FakeStore { Rows = rows };
        var service = Service(Settings(), store);
        var page = await service.ListAsync(0, CancellationToken.None);
        var newBytes = JsonSerializer.SerializeToUtf8Bytes(page, Program.CreateToolJsonOptions()).Length;
        var body = new string('中', 1000);
        for (var id = 1; id <= 3; id++)
        {
            store.Detail = new(id, "", rows[id - 1].Summary, true, body);
            var detail = await service.DetailsAsync(id, CancellationToken.None);
            Assert.False(detail.IsError);
            newBytes += JsonSerializer.SerializeToUtf8Bytes(detail, Program.CreateToolJsonOptions()).Length;
        }
        var oldBytes = JsonSerializer.SerializeToUtf8Bytes(new { desp = string.Join("\n\n", rows.Select(_ => body)) }, Program.CreateToolJsonOptions()).Length;
        Assert.True(newBytes < oldBytes);
        Assert.Equal(1, store.Reads);
        Assert.Equal(3, store.DetailReads); // Four new calls rather than one full-text call; no claim of fewer calls.
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    private static CapabilityService Service(McpSettings settings, FakeStore store) => new(settings, store, NullLogger<CapabilityService>.Instance);
    internal static McpSettings Settings(bool ad = true, bool enabled = true, string check = "Db.dbo.check", int pageSize = 100) => new()
    {
        Connection = new() { Authentication = ad ? "windowsIntegrated" : "sqlPassword", Server = "test.invalid", Username = ad ? "" : "test", Password = ad ? "" : "test-only" },
        Capabilities = new() { ListFunction = enabled ? "Db.dbo.list" : "", CheckFunction = check, PageSize = pageSize },
        Query = new() { MaxResultSizeKb = 16 },
    };

    private sealed class FakeStore : ICapabilityStore
    {
        public Task<bool> IsProcedureGrantedAsync(string database, string schema, string name, CancellationToken token) => CheckAsync(token);
        public CapabilityDetail? Detail { get; set; }
        public int DetailReads { get; private set; }
        public Exception? DetailFailure { get; init; }
        public Task<CapabilityDetail?> ReadDetailsAsync(int id, int maximumCharacters, CancellationToken token)
        {
            DetailReads++;
            return DetailFailure is null ? Task.FromResult(Detail) : Task.FromException<CapabilityDetail?>(DetailFailure);
        }
        public bool Allowed { get; set; }
        public Exception? CheckFailure { get; init; }
        public CapabilityRow[] Rows { get; init; } = [];
        public int Checks { get; private set; }
        public int Reads { get; private set; }
        public (long, int) LastRequest { get; private set; }
        public Task<bool> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            return CheckFailure is null ? Task.FromResult(Allowed) : Task.FromException<bool>(CheckFailure);
        }
        public async IAsyncEnumerable<CapabilityRow> ReadAsync(long offset, int take, int maximumCharacters,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Reads++;
            LastRequest = (offset, take);
            await Task.CompletedTask;
            foreach (var row in Rows.Skip((int)Math.Min(offset, int.MaxValue)).Take(take)) yield return row;
        }
    }
}
