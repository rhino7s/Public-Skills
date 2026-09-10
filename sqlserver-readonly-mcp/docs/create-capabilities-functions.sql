/*
能力目录函数：在已建立三张 tools_* 表的配置数据库中执行。
适用于 SQL Server 2016 SP1 及以上版本（CREATE OR ALTER）。
请在 SSMS 中确认当前数据库，选中整个文件执行；GO 是客户端批次分隔符。
脚本创建或更新三个函数，不授予权限，不修改表和资料。

普通用户只授予：
  GRANT SELECT ON OBJECT::dbo.list_capabilities TO [数据库用户或角色];
  GRANT EXECUTE ON OBJECT::dbo.list_capabilities_check TO [数据库用户或角色];
同时须有配置库有效 CONNECT；不要授予 base、底表或 schema 级 SELECT。
函数与底表应保持相同所有者，使用同库所有权链。
*/
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER FUNCTION dbo.list_capabilities_base
(
    @u_name nvarchar(128)
)
RETURNS TABLE
AS
RETURN
(
    WITH assigned_tools AS
    (
        SELECT rg.tool_id, MIN(rg.ord) AS ord
        FROM dbo.tools_grant AS g
        INNER JOIN dbo.tools_role_group AS rg ON rg.rname = g.rname
        WHERE g.u_name = @u_name
          AND g.active = 1
        GROUP BY rg.tool_id
    )
    SELECT
        i.id,
        a.ord,
        CASE WHEN i.itype = 'grant' THEN i.iname ELSE '' END AS iname,
        CAST(
            CASE i.itype
                WHEN 'skill' THEN N'### SKILL'
                WHEN 'grant' THEN N'### OBJECT: ' + CONVERT(nvarchar(150), i.iname)
            END
            AS nvarchar(max)
        ) + NCHAR(13) + NCHAR(10) + NCHAR(13) + NCHAR(10) + i.desp AS desp
    FROM assigned_tools AS a
    INNER JOIN dbo.tools_info AS i ON i.id = a.tool_id
    WHERE i.active = 1
      AND i.itype IN ('skill', 'grant')
);
GO

CREATE OR ALTER FUNCTION dbo.list_capabilities()
RETURNS TABLE
AS
RETURN
(
    SELECT id, ord, iname, desp
    FROM dbo.list_capabilities_base(ORIGINAL_LOGIN())
);
GO

CREATE OR ALTER FUNCTION dbo.list_capabilities_check()
RETURNS bit
AS
BEGIN
    -- 复用同一有效性逻辑；EXISTS 不输出说明，也不要求排序。
    -- base 为内联表值函数，实际执行计划仍应在部署环境验证。
    IF EXISTS
    (
        SELECT 1
        FROM dbo.list_capabilities_base(ORIGINAL_LOGIN())
    )
        RETURN CONVERT(bit, 1);

    RETURN CONVERT(bit, 0);
END;
GO

/*
部署后按需单独执行（以下不自动运行）：

-- 管理员读取指定用户目录；这里只模拟目录，不模拟该用户的数据库权限。
SELECT id, ord, iname, desp
FROM dbo.list_capabilities_base(N'DOMAIN\example_user')
ORDER BY ord, id;

-- 本人目录和访问资格。
SELECT id, ord, iname, desp FROM dbo.list_capabilities() ORDER BY ord, id;
SELECT dbo.list_capabilities_check() AS can_access;

-- 可选的连续展示序号；不覆盖管理员原始 ord，也不替代 id。
SELECT ROW_NUMBER() OVER (ORDER BY ord, id) AS row_no, id, ord, iname, desp
FROM dbo.list_capabilities()
ORDER BY ord, id;
*/
