# Public-Skills

本仓库集中维护多个独立项目，共用一个 Git 仓库。各项目按需求独立使用，不需要全部安装。

## 项目目录

| 项目 | 功能 | 使用说明 |
| --- | --- | --- |
| `sqlserver-readonly-mcp` | SQL Server MCP 服务，提供受限只读查询、对象定位、定义读取及单独授权的存储过程调用。 | [项目说明](sqlserver-readonly-mcp/README.md) · [Agent 安装说明](sqlserver-readonly-mcp/docs/agent-install.md) |
| `dab-mcp-skill` | 指导 Agent 通过 `dab-sql` MCP 查询财报、市场共识、估值和区间涨跌幅的 Skill；使用时需具备相应 MCP 连接。 | [Skill 说明](dab-mcp-skill/SKILL.md) |

## 安装与使用范围

- 用户指定某个项目时，只安装或配置该项目，并先完整阅读其使用说明。
- 不得将“安装其中一个项目”理解为安装整个仓库，也不得顺带安装、启用或修改其他项目。
- 用户没有明确指定项目时，先确认所需功能和目标项目，不默认安装全部项目。
- 不同项目的安装方式、配置和依赖以各自文档为准，不得相互套用。
- 下载或克隆整个仓库不代表获准安装其中所有项目。

以后新增的项目也遵循以上规则，并在项目目录中补充入口。
