/*
管理员只读诊断。在配置库中执行；不通过普通 MCP 执行。
前提：tools_role_group.active 已迁移，按三层 active 判定有效目录授权。
只写本地临时表，不调用业务过程，不授予或撤销权限。
需要枚举全部数据库/对象及模拟目标 LOGIN 的权限；建议独立管理员会话运行。
@UserName = NULL 检查 tools_grant 中全部账号，否则仅检查指定完整 Login。
报告仅验证入口权限，不保证过程内部依赖、业务逻辑或行级数据范围。
无法模拟的 AD 组成员必须另用本人连接验证；本脚本不会创建 Login。
系统过程不纳入“目录外业务过程”判断；输出每库系统过程数量供范围审阅。
*/
SET NOCOUNT ON;
DECLARE @UserName nvarchar(128) = NULL;

CREATE TABLE #cap_findings
(
    u_name nvarchar(128) COLLATE DATABASE_DEFAULT NULL,
    db_name nvarchar(128) COLLATE DATABASE_DEFAULT NULL,
    object_name nvarchar(776) COLLATE DATABASE_DEFAULT NULL,
    finding varchar(60) NOT NULL,
    detail nvarchar(2048) NULL
);
CREATE TABLE #cap_users (u_name nvarchar(128) COLLATE DATABASE_DEFAULT PRIMARY KEY);
INSERT #cap_users SELECT DISTINCT u_name FROM dbo.tools_grant
WHERE @UserName IS NULL OR u_name = @UserName;
IF @UserName IS NOT NULL AND NOT EXISTS (SELECT 1 FROM #cap_users)
    INSERT #cap_users VALUES (@UserName);

CREATE TABLE #cap_targets
(
    u_name nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL,
    iname nvarchar(150) COLLATE DATABASE_DEFAULT NOT NULL,
    expected bit NOT NULL
);
INSERT #cap_targets
SELECT g.u_name, i.iname,
       CONVERT(bit, MAX(CASE WHEN g.active = 1 AND rg.active = 1 AND i.active = 1 THEN 1 ELSE 0 END))
FROM dbo.tools_grant AS g
JOIN #cap_users AS u ON u.u_name = g.u_name
JOIN dbo.tools_role_group AS rg ON rg.rname = g.rname
JOIN dbo.tools_info AS i ON i.id = rg.tool_id
WHERE i.itype = 'grant'
GROUP BY g.u_name, i.iname;

INSERT #cap_findings
SELECT g.u_name, DB_NAME(), g.rname, 'empty_role', N'授权关联的角色没有工具成员。'
FROM dbo.tools_grant AS g JOIN #cap_users AS u ON u.u_name = g.u_name
WHERE NOT EXISTS (SELECT 1 FROM dbo.tools_role_group AS rg WHERE rg.rname = g.rname);
INSERT #cap_findings
SELECT NULL, DB_NAME(), i.iname, 'invalid_tool', N'工具类型、说明或对象名不符合目录约定。'
FROM dbo.tools_info AS i
WHERE i.itype NOT IN ('skill', 'grant') OR LEN(LTRIM(RTRIM(i.desp))) = 0
   OR (i.itype = 'skill' AND DATALENGTH(i.iname) <> 0)
   OR (i.itype = 'grant' AND (PARSENAME(i.iname, 4) IS NOT NULL OR PARSENAME(i.iname, 3) IS NULL
       OR PARSENAME(i.iname, 2) IS NULL OR PARSENAME(i.iname, 2) <> N'dbo' OR PARSENAME(i.iname, 1) IS NULL));

CREATE TABLE #cap_objects
(
    object_id int PRIMARY KEY,
    object_name nvarchar(517) COLLATE DATABASE_DEFAULT NOT NULL,
    permission_name varchar(10) NOT NULL,
    object_type char(2) NOT NULL
);
CREATE TABLE #cap_resolved
(
    u_name nvarchar(128) COLLATE DATABASE_DEFAULT NOT NULL,
    object_id int NOT NULL,
    expected bit NOT NULL
);
CREATE TABLE #cap_actual
(
    object_id int NOT NULL,
    allowed int NULL,
    partial_select bit NOT NULL
);
CREATE TABLE #cap_databases
(
    name nvarchar(128) COLLATE DATABASE_DEFAULT PRIMARY KEY,
    state_desc nvarchar(60),
    inventory_complete bit NOT NULL DEFAULT 0,
    system_procedures int NULL
);
INSERT #cap_databases(name, state_desc) SELECT name, state_desc FROM sys.databases;
IF COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DATABASE'), 0) <> 1
    INSERT #cap_findings VALUES (NULL, NULL, NULL, 'inventory_unverified', N'当前管理员无法确认全部数据库可见性。');

DECLARE @Db nvarchar(128), @State nvarchar(60), @Sql nvarchar(max), @Login nvarchar(128);
DECLARE cap_db CURSOR LOCAL FAST_FORWARD FOR SELECT name, state_desc FROM #cap_databases;
OPEN cap_db;
FETCH NEXT FROM cap_db INTO @Db, @State;
WHILE @@FETCH_STATUS = 0
BEGIN
    TRUNCATE TABLE #cap_objects;
    TRUNCATE TABLE #cap_resolved;
    IF @State <> N'ONLINE'
        INSERT #cap_findings VALUES (NULL, @Db, NULL, 'database_unverified', @State);
    ELSE
    BEGIN
        BEGIN TRY
            -- Run name comparisons in each target database's own collation.
            SET @Sql = N'USE ' + QUOTENAME(@Db) + N';
IF COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), ''DATABASE'', ''VIEW DEFINITION''), 0) <> 1
    THROW 50010, N''无法完整枚举数据库对象。'', 1;
INSERT #cap_objects
SELECT o.object_id, QUOTENAME(s.name) + N''.'' + QUOTENAME(o.name),
       CASE WHEN o.type IN (''P'', ''PC'', ''FN'', ''FS'') THEN ''EXECUTE'' ELSE ''SELECT'' END, o.type
FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0 AND
      (o.type IN (''P'', ''PC'') OR (o.type IN (''U'', ''V'', ''FN'', ''FS'', ''IF'', ''TF'', ''FT'') AND EXISTS
      (SELECT 1 FROM #cap_targets AS t
       WHERE PARSENAME(t.iname, 3) COLLATE DATABASE_DEFAULT = DB_NAME()
         AND PARSENAME(t.iname, 2) COLLATE DATABASE_DEFAULT = s.name
         AND PARSENAME(t.iname, 1) COLLATE DATABASE_DEFAULT = o.name)));
INSERT #cap_resolved
SELECT t.u_name, o.object_id, CONVERT(bit, MAX(CONVERT(int, t.expected)))
FROM #cap_targets AS t JOIN sys.schemas AS s ON s.name = PARSENAME(t.iname, 2) COLLATE DATABASE_DEFAULT
JOIN sys.objects AS o ON o.schema_id = s.schema_id AND o.name = PARSENAME(t.iname, 1) COLLATE DATABASE_DEFAULT
JOIN #cap_objects AS inventory ON inventory.object_id = o.object_id
WHERE PARSENAME(t.iname, 3) COLLATE DATABASE_DEFAULT = DB_NAME()
GROUP BY t.u_name, o.object_id;
INSERT #cap_findings
SELECT t.u_name, DB_NAME(), t.iname, ''object_missing_or_unsupported'', N''目标对象不存在、类型不支持或属于系统对象。''
FROM #cap_targets AS t
WHERE PARSENAME(t.iname, 3) COLLATE DATABASE_DEFAULT = DB_NAME()
AND NOT EXISTS (SELECT 1 FROM sys.objects AS o JOIN sys.schemas AS s ON s.schema_id=o.schema_id
 JOIN #cap_objects AS inventory ON inventory.object_id=o.object_id
 WHERE s.name=PARSENAME(t.iname,2) COLLATE DATABASE_DEFAULT AND o.name=PARSENAME(t.iname,1) COLLATE DATABASE_DEFAULT);
UPDATE #cap_databases SET inventory_complete=1,
 system_procedures=(SELECT COUNT(*) FROM sys.system_objects WHERE type IN (''P'',''PC'',''X''))
WHERE name=@db;';
            EXEC sys.sp_executesql @Sql, N'@db nvarchar(128)', @db=@Db;

            DECLARE cap_user CURSOR LOCAL FAST_FORWARD FOR SELECT u_name FROM #cap_users;
            OPEN cap_user;
            FETCH NEXT FROM cap_user INTO @Login;
            WHILE @@FETCH_STATUS = 0
            BEGIN
                TRUNCATE TABLE #cap_actual;
                BEGIN TRY
                    -- Escape the login literal; impersonation is scoped inside this dynamic batch.
                    SET @Sql = N'EXECUTE AS LOGIN = N''' + REPLACE(@Login, N'''', N'''''') + N''';
BEGIN TRY
 USE ' + QUOTENAME(@Db) + N';
 INSERT #cap_actual
 SELECT o.object_id, HAS_PERMS_BY_NAME(o.object_name, ''OBJECT'', o.permission_name),
 CONVERT(bit, CASE WHEN o.permission_name=''SELECT'' AND EXISTS
 (SELECT 1 FROM sys.columns AS c WHERE c.object_id=o.object_id
 AND HAS_PERMS_BY_NAME(o.object_name,''OBJECT'',''SELECT'',c.name,''COLUMN'')=1) THEN 1 ELSE 0 END)
 FROM #cap_objects AS o;
END TRY
BEGIN CATCH
 REVERT;
 THROW;
END CATCH;
REVERT;';
                    EXEC sys.sp_executesql @Sql;

                    INSERT #cap_findings
                    SELECT @Login, @Db, o.object_name,
                        CASE WHEN a.allowed IS NULL THEN 'permission_unverified'
                             WHEN r.expected=1 AND a.allowed=0 AND a.partial_select=1 THEN 'partial_column_access'
                             WHEN r.expected=1 AND a.allowed=0 THEN 'expected_permission_missing'
                             WHEN r.expected=0 THEN 'inactive_permission_remains'
                             ELSE 'unlisted_procedure_execute' END,
                        N'入口权限：' + o.permission_name + N'；请根据账号用途审阅差异。'
                    FROM #cap_objects AS o JOIN #cap_actual AS a ON a.object_id=o.object_id
                    LEFT JOIN #cap_resolved AS r ON r.object_id=o.object_id AND r.u_name=@Login
                    WHERE (r.expected=1 AND (a.allowed=0 OR a.allowed IS NULL))
                       OR (r.expected=0 AND (a.allowed=1 OR a.partial_select=1))
                       OR (r.object_id IS NULL AND o.object_type IN ('P','PC') AND a.allowed=1)
                       OR (a.allowed IS NULL AND o.object_type IN ('P','PC'));
                END TRY
                BEGIN CATCH
                    INSERT #cap_findings VALUES (@Login, @Db, NULL, 'login_database_unverified', ERROR_MESSAGE());
                END CATCH;
                FETCH NEXT FROM cap_user INTO @Login;
            END;
            CLOSE cap_user;
            DEALLOCATE cap_user;
        END TRY
        BEGIN CATCH
            INSERT #cap_findings VALUES (NULL, @Db, NULL, 'database_unverified', ERROR_MESSAGE());
        END CATCH;
    END;
    FETCH NEXT FROM cap_db INTO @Db, @State;
END;
CLOSE cap_db;
DEALLOCATE cap_db;

INSERT #cap_findings
SELECT t.u_name, PARSENAME(t.iname, 3), t.iname, 'database_missing_or_unseen', N'目标数据库不存在或未被当前管理员枚举。'
FROM #cap_targets AS t
WHERE NOT EXISTS (SELECT 1 FROM #cap_databases AS d WHERE d.name=PARSENAME(t.iname,3));

SELECT u_name, db_name, object_name, finding, detail FROM #cap_findings
ORDER BY u_name, db_name, finding, object_name;
SELECT name AS db_name, state_desc, inventory_complete, system_procedures FROM #cap_databases ORDER BY name;
SELECT u_name AS inspected_login FROM #cap_users ORDER BY u_name;

DROP TABLE #cap_actual;
DROP TABLE #cap_resolved;
DROP TABLE #cap_objects;
DROP TABLE #cap_databases;
DROP TABLE #cap_targets;
DROP TABLE #cap_users;
DROP TABLE #cap_findings;
