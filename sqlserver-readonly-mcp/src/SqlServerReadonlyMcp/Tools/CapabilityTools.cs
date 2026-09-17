using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tools;

[McpServerToolType]
public sealed class CapabilityTools(CapabilityService capabilities)
{
    [McpServerTool(Name = "list_capabilities", Title = "读取能力目录摘要", ReadOnly = true,
        Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(CapabilityPage))]
    [Description("""
        返回“能力目录”摘要：id、iname、summary、has_desp；iname 为业务对象名称，说明类条目为空。
        分页字段为 has_more 和 next_offset。
        """)]
    public Task<CallToolResult> ListAsync(
        [Description("跳过的摘要条数；续页位置为 next_offset。")]
        long offset = 0, CancellationToken cancellationToken = default) => capabilities.ListAsync(offset, cancellationToken);

    [McpServerTool(Name = "get_capability_details", Title = "读取能力详情", ReadOnly = true,
        Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(CapabilityDetailsResult))]
    [Description("""
        返回“能力目录”中指定条目的完整说明 desp；没有正文时 has_desp=false。
        """)]
    public Task<CallToolResult> DetailsAsync(
        [Description("list_capabilities 返回的条目 id，正整数。")]
        int id, CancellationToken cancellationToken = default) => capabilities.DetailsAsync(id, cancellationToken);
}
