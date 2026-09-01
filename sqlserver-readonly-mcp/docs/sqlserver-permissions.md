# SQL Server 权限

SQL Server 低权限账号是 MCP 直接数据库访问的最终安全边界。建议由 DBA 建立只含 `public` 服务器角色的专用 Login，再按实际用途逐个数据库授权。

## 业务数据库

```sql
USE [ExampleDatabase];

IF USER_ID(N'<readonly_login>') IS NULL
    CREATE USER [<readonly_login>] FOR LOGIN [<readonly_login>];

ALTER ROLE [db_datareader] ADD MEMBER [<readonly_login>];
ALTER ROLE [db_denydatawriter] ADD MEMBER [<readonly_login>];
GRANT VIEW DEFINITION TO [<readonly_login>];
```

- `db_datareader`：读取用户表和视图。
- `db_denydatawriter`：拒绝直接写入用户表。
- `VIEW DEFINITION`：读取对象定义，不授予资料读取或过程执行权限。

不要授予 `sysadmin`、其他服务器角色、`db_owner`、`db_ddladmin` 或数据库级 `GRANT EXECUTE`。同时应复核 `public` 和历史对象级授权。

## 存储过程

只对审核完成的过程授予对象级权限：

```sql
USE [ExampleDatabase];

GRANT EXECUTE ON OBJECT::[dbo].[ExampleProcedure]
    TO [<readonly_login>];

-- 不再允许执行时：
REVOKE EXECUTE ON OBJECT::[dbo].[ExampleProcedure]
    FROM [<readonly_login>];
```

`db_denydatawriter` 不能限制已授权存储过程的内部行为。Ownership Chaining、`EXECUTE AS`、模块签名或动态 SQL 都可能改变实际权限；授权前必须审核定义、调用链和执行上下文。需要绝对只读时，应连接只读副本或报表库。

表值函数通常可由 `db_datareader` 覆盖，标量函数可能需要对象级 `EXECUTE` 或 `REFERENCES`，应按实际对象单独授权。

## SQL Server Agent Job

只有 `find_object_references` 确实需要搜索 Job 时，才在 `msdb` 授予以下两张表的读取权限：

```sql
USE [msdb];

IF USER_ID(N'<readonly_login>') IS NULL
    CREATE USER [<readonly_login>] FOR LOGIN [<readonly_login>];

GRANT SELECT ON OBJECT::[dbo].[sysjobs]
    TO [<readonly_login>];

GRANT SELECT ON OBJECT::[dbo].[sysjobsteps]
    TO [<readonly_login>];

ALTER ROLE [db_denydatawriter] ADD MEMBER [<readonly_login>];
```

不要为了 Job 查询加入权限更宽的 `SQLAgentReaderRole` 或 `db_datareader`。`sysjobsteps.command` 可能包含内部路径、账号或其他敏感参数，只应授权给专用 MCP Login。

## 检查与撤销

配置后运行 [check-access.sql](check-access.sql)，检查当前身份、数据库访问、角色、对象定义可见性和有效权限。

删除数据库 User 不会删除服务器 Login。确认不再使用后，可按数据库撤销：

```sql
USE [ExampleDatabase];

REVOKE VIEW DEFINITION FROM [<readonly_login>];
ALTER ROLE [db_denydatawriter] DROP MEMBER [<readonly_login>];
ALTER ROLE [db_datareader] DROP MEMBER [<readonly_login>];
DROP USER [<readonly_login>];
```

只有确认所有数据库映射均已清理后，才由 DBA 另行删除服务器 Login。
