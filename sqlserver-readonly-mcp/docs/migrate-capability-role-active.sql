/* 管理员在配置库执行，然后部署 create-capabilities-functions.sql。
   仅新增关联启用字段；已有记录默认启用，不修改已有 active 值或 SQL 权限。
   可重复执行，已有字段契约不符时停止，不覆盖已有定义。 */
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.tools_role_group', N'U') IS NULL
    THROW 50000, N'tools_role_group 不存在，请使用首次建表脚本。', 1;
IF COL_LENGTH(N'dbo.tools_role_group', N'active') IS NULL
    ALTER TABLE dbo.tools_role_group ADD active bit NOT NULL
        CONSTRAINT DF_tools_role_group_active DEFAULT (1) WITH VALUES;
IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.tools_role_group') AND name = N'active'
      AND system_type_id = 104 AND user_type_id = 104
      AND is_nullable = 0 AND is_computed = 0
)
    THROW 50000, N'tools_role_group.active 必须为 bit NOT NULL 普通列，请管理员核对。', 1;
IF NOT EXISTS
(
    SELECT 1 FROM sys.columns AS c
    INNER JOIN sys.default_constraints AS d ON d.object_id = c.default_object_id
    WHERE c.object_id = OBJECT_ID(N'dbo.tools_role_group') AND c.name = N'active'
      AND REPLACE(REPLACE(REPLACE(d.definition, N'(', N''), N')', N''), N' ', N'') = N'1'
)
    THROW 50000, N'tools_role_group.active 默认值应为 1，请管理员核对。', 1;
PRINT N'角色组关联启用字段就绪，请继续部署 create-capabilities-functions.sql。';
