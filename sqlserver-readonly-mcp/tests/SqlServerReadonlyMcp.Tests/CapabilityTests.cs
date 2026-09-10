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
        Assert.True(await service.CanAccessAsync(CancellationToken.None));
        store.Allowed = false;
        Assert.False(await service.CanAccessAsync(CancellationToken.None));
        store.Allowed = true;
        Assert.True(await service.CanAccessAsync(CancellationToken.None));
        Assert.Equal(3, store.Checks);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SqlOrDisabledCatalogSkipsCheck(bool ad, bool enabled)
    {
        var store = new FakeStore { CheckFailure = new UnauthorizedAccessException() };
        var service = Service(Settings(ad: ad, enabled: enabled), store);
        Assert.True(await service.CanAccessAsync(CancellationToken.None));
        Assert.Equal(0, store.Checks);
    }

    [Fact]
    public async Task PermissionAndContractFailuresDenyWithoutLeakingDiagnostics()
    {
        foreach (var error in new Exception[] { new UnauthorizedAccessException("secret database"), new InvalidDataException(), new TimeoutException() })
        {
            var service = Service(Settings(), new FakeStore { CheckFailure = error });
            Assert.False(await service.CanAccessAsync(CancellationToken.None));
            var result = CapabilityService.Denied();
            Assert.Equal("用户没有访问权限", Text(result));
            Assert.Equal("access_denied", result.StructuredContent!.Value.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task PagesMarkdownWithoutDuplicatingStructuredContent()
    {
        var store = new FakeStore { Rows = [new(7, -5, "### SKILL\n\nrule"), new(2, 99, "### 对象：Db.dbo.p\n\nbody"), new(3, 99, "last")] };
        var service = Service(Settings(pageSize: 2), store);
        var first = await service.ListAsync(0, CancellationToken.None);
        Assert.Null(first.StructuredContent);
        Assert.Contains("### SKILL", Text(first));
        Assert.Contains("### 对象：Db.dbo.p", Text(first));
        Assert.DoesNotContain("last", Text(first));
        Assert.Contains("has_more=true；next_offset=2", Text(first));
        var second = await service.ListAsync(2, CancellationToken.None);
        Assert.Contains("last", Text(second));
        Assert.Contains("has_more=false；next_offset=null", Text(second));
        Assert.Equal((2L, 3), store.LastRequest);
    }

    [Fact]
    public async Task SizeLimitStopsAtWholeRowAndNextPageReportsOversizedItem()
    {
        var store = new FakeStore { Rows = [new(1, 0, "first"), new(2, 0, new string('中', 20_000))] };
        var service = Service(Settings(), store);
        var first = await service.ListAsync(0, CancellationToken.None);
        Assert.Contains("next_offset=1", Text(first));
        Assert.DoesNotContain("中", Text(first));
        var second = await service.ListAsync(1, CancellationToken.None);
        Assert.True(second.IsError);
        Assert.Equal("capability_too_large", second.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first, Program.CreateToolJsonOptions()).Length < 16 * 1024);
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
            Assert.Equal("用户没有访问权限", Text(result));
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
