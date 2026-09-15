using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tools;

[McpServerToolType]
public sealed class CapabilityTools(CapabilityService capabilities)
{
    [McpServerTool(Name = "list_capabilities", Title = "读取业务使用说明", ReadOnly = true,
        Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(CapabilityPage))]
    [Description("返回当前身份的能力摘要；has_more=true 时按 next_offset 续取至完整。适用条目 has_desp=true 时，使用前调用 get_capability_details 读取详情。")]
    public Task<CallToolResult> ListAsync(
        [Description("跳过的说明条数，默认 0；后续使用上次返回的 next_offset。")]
        long offset = 0, CancellationToken cancellationToken = default) => capabilities.ListAsync(offset, cancellationToken);

    [McpServerTool(Name = "get_capability_details", Title = "读取能力详情", ReadOnly = true,
        Destructive = false, Idempotent = false, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(CapabilityDetailsResult))]
    [Description("按摘要目录中的 id 读取适用能力的完整说明。has_desp=false 时以 summary 为完整说明。对象字段、参数类型和 SQL 定义使用 get_object_details 获取。")]
    public Task<CallToolResult> DetailsAsync(
        [Description("摘要目录返回的能力 id，必须为正整数。")]
        int id, CancellationToken cancellationToken = default) => capabilities.DetailsAsync(id, cancellationToken);
}
