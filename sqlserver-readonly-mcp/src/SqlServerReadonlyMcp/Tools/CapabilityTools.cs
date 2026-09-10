using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlServerReadonlyMcp.Sql;

namespace SqlServerReadonlyMcp.Tools;

[McpServerToolType]
public sealed class CapabilityTools(CapabilityService capabilities)
{
    [McpServerTool(Name = "list_capabilities", Title = "读取业务使用说明", ReadOnly = true,
        Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("返回当前身份的业务使用说明；has_more=true 时按 next_offset 续取至完整。")]
    public Task<CallToolResult> ListAsync(
        [Description("跳过的说明条数，默认 0；后续使用上次返回的 next_offset。")]
        long offset = 0, CancellationToken cancellationToken = default) => capabilities.ListAsync(offset, cancellationToken);
}
