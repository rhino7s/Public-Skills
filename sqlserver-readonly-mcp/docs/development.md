# 开发与发布

以下命令在 sqlserver-readonly-mcp 项目目录执行，另有注明的除外。本文面向维护者，不随安装包提供。

源码已实现目录受限与开发查询两种访问模式，见 [配置迁移与支持范围](access-modes.md)、[目录契约](capabilities.md) 和 [验证记录](access-modes-validation.md)。开发构建不代表当前 Release 已包含变更。

```powershell
.\check-public-repo.ps1
dotnet restore SqlServerReadonlyMcp.slnx
dotnet build SqlServerReadonlyMcp.slnx --no-restore
.\publish-all.ps1
dotnet test SqlServerReadonlyMcp.slnx --no-restore --no-build
```

Linux/macOS 使用 `publish-all.sh`。发布结果位于被 Git 忽略的 `publish/<rid>`。

从本机向 GitHub 推送本项目变更或发布新版时，先运行 `publish-all.ps1`（Linux/macOS 使用 `publish-all.sh`），确认本地发布成功，再完成测试及发布产物验证，最后推送或发布。GitHub 推送和 Release 工作流本身不会更新本机的 `publish/<rid>`；此步骤也不会更新其他安装目录或重启正在运行的 MCP。

Windows x64 GitHub Release 由 [发布工作流](https://github.com/rhino7s/Public-Skills/actions/workflows/release-sqlserver-readonly-mcp.yml) 自动建立。标签必须使用 `sqlserver-readonly-mcp-v<项目版本>`，例如 `sqlserver-readonly-mcp-v0.9.0`；标签版本必须与项目文件中的 `Version` 完全一致。带预发布后缀的版本（例如 `0.10.0-rc.1`）会发布为 Pre-release，且不会替代正式 Latest。云端工作流从标签提交构建、测试，并从全新暂存目录按固定白名单生成 ZIP，不使用开发机的 `publish/`、日志或本地配置。

在已提交并推送版本变更后，可从仓库根目录运行 `pwsh -NoProfile -File ./sqlserver-readonly-mcp/push-release-tag.ps1 -Version <版本>`。脚本只允许在干净的 `main`、`HEAD=origin/main`、固定远端及 noreply Git 邮箱下运行；本机检查、构建和测试通过后，只新建并推送对应 Release 标签，不会推送分支或强制改写历史。标签推送后，脚本只读查询固定 GitHub 仓库，等待 workflow 完成并校验 Release 的标签、预发布状态及两个固定资产。

若本机同时存在 `appsettings.local.json` 和 `integration.local.json`，发布脚本会自动运行 `test-integration.ps1`。否则必须明确添加且只能添加一个参数：已对当前提交完成真实测试时使用 `-ConfirmIntegrationTestsCompleted`；本次没有数据库访问逻辑变更时使用 `-ConfirmIntegrationTestsNotRequired`。没有测试或确认时不会创建标签。

本机存在 `appsettings.local.json` 时，数据库访问逻辑变更必须在提交或发布前使用被 Git 忽略的 `integration.local.json` 执行真实只读测试：

```powershell
.\test-integration.ps1
```

Inspector 使用 `publish/<rid>` 下的程序和项目内的 `appsettings.local.json`。验证本地发布程序及配置时，应先断开 Inspector 并停止由它启动的旧服务进程，完成本地发布，再通过 `start-inspector.ps1` 或 `start-inspector.sh` 启动并重新连接，确认工具列表及用户指定的低成本只读查询。不得用仍在运行的旧进程作为新版验证结果；Inspector 只应在本机使用。

普通自动协议测试启动的是测试构建中的程序集。`test-integration.ps1` 则先重新发布当前平台程序，再通过 `publish/<rid>` 中的程序和指定的本机配置测试 MCP，包括 `execute_sql` 的低成本连接查询；发布失败立即停止，不回退到旧程序。脚本输出被测程序路径、SHA-256 和配置路径，便于核对测试范围。测试通过仅适用于该程序和该配置，不能代表其他配置或进程也能连接。

