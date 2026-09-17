namespace SqlServerReadonlyMcp.Sql;

// 对外错误说明；不修改供执行判断和审计使用的原始错误。
internal static class PublicToolErrors
{
    internal const string ConnectionFailed = "连接失败，请确认网络连接后再试。";
    internal const string AccessDenied = "用户没有访问权限";

    internal static ToolError Present(ToolError error)
    {
        var category = error.Category;
        string message;
        if (category == "execution_unknown")
            message = "无法确认执行是否完成，可能已有部分操作生效；不得自动重试。";
        else if (category is "access_denied" or "permission_denied" ||
                 error.SqlErrorNumber is 229 or 230 or 262 or 300 or 916)
        {
            category = "access_denied";
            message = AccessDenied;
        }
        else if (category is "connection_error" or "authentication_or_database" ||
                 error.Message == ConnectionFailed)
            message = ConnectionFailed;
        else
            message = category switch
            {
                "internal_error" => "服务暂时不可用。",
                "timeout" when error.SqlErrorNumber is not null => "请求超时。",
                "object_not_available" or "procedure_not_available" => "查询目标不可用，本次操作未执行。",
                "sql_error" => error.SqlErrorNumber switch
                {
                    245 or 8114 => "数据类型转换失败，请检查输入值与目标类型。",
                    8115 => "计算结果超出数据类型范围。",
                    8134 => "计算中出现除以零。",
                    207 => "查询引用了无效字段，请核对字段名称。",
                    208 or 2812 => "查询目标不可用。",
                    _ => "查询执行失败。",
                },
                _ => error.Message,
            };
        return new(category, message);
    }
}
