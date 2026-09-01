namespace SqlServerReadonlyMcp;

internal static class McpServerInstructions
{
    public const string Text = """
        仅处理用户明确指定的业务数据库任务；每次调用必须明确数据库范围及目标对象或待核对问题，禁止无目的枚举、搜索或大范围取数。

        按工具说明选择最小范围操作；truncated、任一 HasMore=true 或 TruncationReason 非空的结果不得视为完整。execute_procedure 仅在用户明确要求该业务动作且已确认 canExecute=true 时使用。

        结论应区分事实、推断与限制；数值结论说明来源、公式、期间、口径、币种和单位。证据足够即停止。
        """;
}
