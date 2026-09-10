/*
用户能力目录：首次建表脚本。
先把下方两处数据库名替换为同一个实际独立配置库，在 SSMS/sqlcmd 中由管理员执行。
不创建数据库、不修改已有表、不部署函数、不授予权限。
任何同名表已存在时整批停止，不覆盖已有资料。
详见 capabilities-plan.md。
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
        desp nvarchar(max) NOT NULL,
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
        remark nvarchar(max) NULL,
        upd_time datetime NOT NULL
            CONSTRAINT DF_tools_grant_upd_time DEFAULT (GETDATE()),
        active bit NOT NULL
            CONSTRAINT DF_tools_grant_active DEFAULT (1),
        CONSTRAINT UQ_tools_grant_user_role UNIQUE (u_name, rname)
    );

    CREATE INDEX IX_tools_grant_user_active_role
        ON dbo.tools_grant(u_name, active, rname);

s