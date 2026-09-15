/*
能力目录函数（摘要/按需详情新版契约）：在已建立三张 tools_* 表的配置数据库中执行。
前提：tools_info 已新增并补齐 summary；desp 为 NOT NULL，无详情填写空白。需与新版 MCP 配套部署。
前提：tools_grant 已新增 ord；已有环境先执行 migrate-capability-grant-order.sql。
前提：tools_role_group 已新增 active；已有环境先执行 migrate-capability-role-active.sql。
get_capability_details(id) 复用无参数 list_capabilities()，通过 WHERE id=@id 读取详情，不新增 SQL 函数。
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
        -- 对同一能力成对选取最靠前的授权位置，不能分别 MIN 两个 ord。
        SELECT rg.tool_id, g.ord AS grant_ord, rg.ord AS role_ord,
               ROW_NUMBER() OVER
               (
                   PARTITION BY rg.tool_id
                   ORDER BY g.ord, rg.ord, g.gid
               ) AS grant_rank
        FROM dbo.tools_grant AS g
        INNER JOIN dbo.tools_role_group AS rg ON rg.rname = g.rname
        WHERE g.u_name = @u_name
          AND g.active = 1
          AND rg.active = 1
    )
    SELECT
        i.id,
        -- 去重并过滤有效条目后，生成最终展示序号；保持 MCP 的 int ord 契约。
        CONVERT(int, ROW_NUMBER() OVER (ORDER BY a.grant_ord, a.role_ord, i.id)) AS ord,
        CASE WHEN i.itype = 'grant' THEN i.iname ELSE '' END AS iname,
        i.summary,
        d.has_desp,
        i.desp
    FROM assigned_tools AS a
    INNER JOIN dbo.tools_info AS i ON i.id = a.tool_id
    -- 原样返回 summary/desp，保留正文 Markdown，不自动添加类型或对象标题。
    -- 无详情请填写空字符串；has_desp 使用原始正文判断。
    OUTER APPLY
(
    SELECT has_desp = CONVERT(bit, CASE WHEN i.desp <> N'' THEN 1 ELSE 0 END)
) AS d
    WHERE a.grant_rank = 1
      AND i.active = 1
      AND i.itype IN ('skill', 'grant')
);
GO

CREATE OR ALTER FUNCTION dbo.list_capabilities()
RETURNS TABLE
AS
RETURN
(
    SELECT id, ord, iname, summary, has_desp, desp
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
SELECT id, ord, iname, summary, has_desp, desp
FROM dbo.list_capabilities_base(N'DOMAIN\example_user')
ORDER BY ord, id;

-- MCP 摘要目录不读取 desp，分页由现有配置控制。
SELECT id, ord, iname, summary, has_desp FROM dbo.list_capabilities() ORDER BY ord, id;

-- MCP 详情工具：id 是查询参数，不是 list_capabilities 的函数参数。
DECLARE @id int = 1;
SELECT id, iname, summary, has_desp, desp FROM dbo.list_capabilities() WHERE id = @id;

-- 本人目录和访问资格。
SELECT id, ord, iname, summary, has_desp, desp FROM dbo.list_capabilities() ORDER BY ord, id;
SELECT dbo.list_capabilities_check() AS can_access;

-- ord 已是去重后的连续展示序号；不修改两张表中的原始权重，也不替代 id。
SELECT id, ord, iname, summary, has_desp
FROM dbo.list_capabilities()
ORDER BY ord, id;
*/
