/*
用户能力目录：首次建表脚本，目标为摘要/按需详情新版契约。
在独立配置库中由管理员选中 SET NOCOUNT ON 起的建表段执行。
前面的 SELECT/RETURN 是手工查看入口；不应整文件直接执行。
不创建数据库、不迁移已有表、不部署函数、不授予权限。
已有 tools_info 的环境应按 cb-practice-improvement-plan.md 分阶段迁移并人工补齐 summary，不能直接重跑 CREATE。
*/

select * from dbo.tools_info
select * from dbo.tools_role_group
select * from dbo.tools_grant

return

SET NOCOUNT ON;


    CREATE TABLE dbo.tools_info
    (
        id int IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_tools_info PRIMARY KEY,
        itype varchar(5) NOT NULL, -- skill / grant
        iname varchar(150) NOT NULL
            CONSTRAINT DF_tools_info_iname DEFAULT (''),
        summary nvarchar(1000) NOT NULL,
        desp nvarchar(max) NOT NULL, -- 没有则给空白值；has_desp 由函数根据原始正文计算
        remark nvarchar(max) NULL,
        upd_time datetime NOT NULL
            CONSTRAINT DF_tools_info_upd_time DEFAULT (GETDATE()),
        active bit NOT NULL
            CONSTRAINT DF_tools_info_active DEFAULT (1)
    );

    -- 仅 grant 要求对象名唯一；多个 skill 的 iname 均为空。
    CREATE UNIQUE INDEX UX_tools_info_grant_iname
        ON dbo.tools_info(iname)
        WHERE itype = 'grant';



    CREATE TABLE dbo.tools_role_group
    (
        rid int IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_tools_role_group PRIMARY KEY,
        rname varchar(50) NOT NULL,
        tool_id int NOT NULL,
        ord int NOT NULL,
        remark nvarchar(max) NULL,
        upd_time datetime NOT NULL
            CONSTRAINT DF_tools_role_group_upd_time DEFAULT (GETDATE()),
        active bit NOT NULL -- 单独控制该角色组内的能力关联
            CONSTRAINT DF_tools_role_group_active DEFAULT (1),
        CONSTRAINT FK_tools_role_group_tools_info
            FOREIGN KEY (tool_id) REFERENCES dbo.tools_info(id)
    );

    CREATE UNIQUE INDEX UX_tools_role_group_rname_tool_id
        ON dbo.tools_role_group(rname, tool_id) INCLUDE (ord);
    CREATE INDEX IX_tools_role_group_tool_id
        ON dbo.tools_role_group(tool_id);

    CREATE TABLE dbo.tools_grant
    (
        gid int IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_tools_grant PRIMARY KEY,
        u_name nvarchar(128) NOT NULL,
        rname varchar(50) NOT NULL,
        ord int NOT NULL -- 用户获授角色组的排序权重，优先于组内 ord
            CONSTRAINT DF_tools_grant_ord DEFAULT (0),
        remark nvarchar(max) NULL,
        upd_time datetime NOT NULL
            CONSTRAINT DF_tools_grant_upd_time DEFAULT (GETDATE()),
        active bit NOT NULL
            CONSTRAINT DF_tools_grant_active DEFAULT (1),
        CONSTRAINT UQ_tools_grant_user_role UNIQUE (u_name, rname)
    );

    CREATE INDEX IX_tools_grant_user_active_role
        ON dbo.tools_grant(u_name, active, rname);
