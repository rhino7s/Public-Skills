namespace SqlServerReadonlyMcp;

internal static class McpServerInstructions
{
    internal static string Build(bool capabilitiesEnabled, bool catalogMode = false)
    {
        var instructions = """
            按用户需求限定查询范围。
            禁止自行编写持久化修改 SQL。
            结果截断或调用失败时，说明已返回结果的范围与限制。
            无法连接时，提示“无法连接内部数据库，请确认网络连接后再试。”，不自动重试。
            访问被拒绝时不得绕过限制。
            """;
        return capabilitiesEnabled
            ? "list_capabilities 是当前授权的“能力目录”，业务操作前读取完整“能力目录”摘要，选择适用条目；has_desp=true 时读取 get_capability_details 并遵守说明。\n" + instructions
            : instructions;
    }
}
