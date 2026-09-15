/*
管理员在配置库执行：仅新增 tools_grant.ord，不部署函数、不修改授权。
已有记录默认为 0，因此在人工调整权重前仍按组内 ord、能力 id 排列。
完成后部署 create-capabilities-functions.sql；MCP 无需改动。
可重复执行；已有 ord 必须为 int NOT NULL 且默认值为 0，不覆盖自定义权重。
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.tools_grant', N'U') IS NULL
    THROW 50000, N'tools_grant 不存在，请使用首次建表脚本。', 1;

IF COL_LENGTH(N'dbo.tools_grant', N'ord') IS NULL
    ALTER TABLE dbo.tools_grant ADD ord int NOT NULL
        CONSTRAINT DF_tools_grant_ord DEFAULT (0) WITH VALUES;

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.tools_grant') AND name = N'ord'
      AND system_type_id = 56 AND user_type_id = 56
      AND is_nullable = 0 AND is_computed = 0 AND is_identity = 0
)
    THROW 50000, N'tools_grant.ord 必须为 int NOT NULL 普通列，请管理员核对。', 1;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns AS c
    INNER JOIN sys.default_constraints AS d ON d.object_id = c.default_object_id
    WHERE c.object_id = OBJECT_ID(N'dbo.tools_grant') AND c.name = N'ord'
      AND REPLACE(REPLACE(REPLACE(d.definition, N'(', N''), N')', N''), N' ', N'') = N'0'
)
    THROW 50000, N'tools_grant.ord 默认值应为 0，请管理员核对；未覆盖已有定义。', 1;

PRINT N'授权排序字段就绪，请继续部署 create-capabilities-functions.sql。';
