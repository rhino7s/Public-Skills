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
    [Description("开始业务操作前读取业务使用说明，并遵守其中的适用规则。按返回的 next_offset 续取至完整。收到用户没有访问权限后停止使用本 MCP，不尝试其他工具路径。")]
    public Task<CallToolResult> ListAsync(
        [Description("跳过的说明条数，默认 0；后续使用上次返回的 next_offset。")]
        long offset = 0, CancellationToken cancellationToken = default) => capabilities.ListAsync(offset, cancellationToken);
}
