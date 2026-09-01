# Agent 通用安装说明

供 Codex、OpenClaw、Claude 等支持本地 `stdio` MCP 的 Agent 使用。不同客户端的配置格式不统一，应把下列参数映射到客户端当前支持的格式。

## 安装

1. 选择当前平台发布包：Windows x64 使用 `win-x64`，Linux x64 使用 `linux-x64`，Intel Mac 使用 `osx-x64`，Apple Silicon 使用 `osx-arm64`。
2. 没有发布包时，安装 .NET 10 SDK，并从源码运行 `publish-all.ps1` 或 `publish-all.sh`。
3. 复制 [appsettings.example.json](../appsettings.example.json) 为 `appsettings.local.json`，由用户填写 SQL Server 低权限账号。不得回显、提交或写入聊天记录。
4. 在 Agent 中建立本地 MCP：

| 参数 | 值 |
| --- | --- |
| Name | `sqlserver-readonly` |
| Transport | `stdio` |
| Command | 当前平台 MCP 可执行文件的绝对路径 |
| Args | `--config`、`appsettings.local.json` 的绝对路径 |

Command 和 Args 不要拼成一段 shell 字符串。Windows 可执行文件以 `.exe` 结尾，Linux/macOS 没有扩展名。

5. Linux/macOS 设置最小文件权限：

```sh
chmod 700 /absolute/path/sqlserver-readonly-mcp
chmod 600 /absolute/path/appsettings.local.json
```

6. 重新加载 MCP 客户端，确认可列出 `execute_sql`、`execute_procedure`、`find_object`、`find_object_references` 和 `get_object_details`。

连接验证必须由用户提供明确数据库和对象，并使用低成本只读操作；不要枚举对象或执行大范围查询。
