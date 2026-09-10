namespace SqlServerReadonlyMcp;

internal static class McpServerInstructions
{
    private const string Common = """
        禁止自行生成或执行修改持久化数据、结构、权限及实例配置的命令，不得绕过限制。允许操作当前连接的本地临时表（#）和表变量。

        仅处理用户明确指定的业务数据库任务，按需求限定范围，禁止无目的枚举、搜索或大范围取数。对象名称不明确时先用 find_object 定位，再进行后续操作。

        按工具说明操作；truncated、任一 HasMore=true 或 TruncationReason 非空的结果不得视为完整，应按续页提示读取或明确说明限制。结论区分事实、推断与限制，证据足够即停止。
        """;

    internal static string Build(bool capabilitiesEnabled) => capabilitiesEnabled
        ? "仅允许只读查询及通过 execute_procedure 调用已授权、用户明确要求的业务 procedure，不得以修改 SQL 替代 procedure。\n\n" + Common +
          "\n\n开始业务操作前读取 list_capabilities，按分页提示读取完整并遵守业务说明。收到 access_denied 后停止调用本 MCP；检查或目录暂不可用时停止本次业务操作，可稍后重试；调用取消后不自动重试。"
        : "仅允许只读查询。\n\n" + Common;
}
