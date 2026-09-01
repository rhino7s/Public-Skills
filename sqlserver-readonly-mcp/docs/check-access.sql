/*==============================================================
  SQL Server Login 最终权限能力检查
  role_sys：
    admin    = CONTROL DATABASE
    ddl      = 业务数据库具有 DATABASE ALTER / ALTER ANY SCHEMA
    executor = DATABASE 范围 EXECUTE
    writer   = INSERT / UPDATE / DELETE
    reader   = SELECT
    reader*  = reader + 特定 Procedure/Schema EXECUTE
  grant：
    VIEW DEFINITION
  execute_details：
    特定 Procedure EXECUTE
    或 Schema EXECUTE
  注意：
    - 将 <readonly_login> 替换为待检查 Login；执行者须有 IMPERSONATE 该 Login 的权限
    - 不检查原始 Role 名称，只看最终有效权限
    - 不扫描 Column-level 权限
    - 不把 guest 当 DatabaseUser
    - 系统数据库不判断 ddl
    - 系统数据库不展开 execute_details
/*
    USE [ExampleDatabase];

    EXECUTE AS LOGIN = '<readonly_login>';

    SELECT
        SUSER_SNAME() AS LoginName,
        USER_NAME() AS DatabaseUser;

    -- Database 层级
    SELECT *
    FROM fn_my_permissions(NULL, 'DATABASE')
    ORDER BY permission_name;

    -- Table 层级
    SELECT *
    FROM fn_my_permissions('dbo.ExampleTable', 'OBJECT')
    ORDER BY permission_name;

    REVERT;
*/
==============================================================*/
DECLARE @LoginName sysname = N'<readonly_login>';
DECLARE @LoginSid varbinary(85) = SUSER_SID(@LoginName);
IF @LoginSid IS NULL
    THROW 50000, N'找不到指定的 SQL Server Login。', 1;
DROP TABLE IF EXISTS #DatabaseCapabilities;
CREATE TABLE #DatabaseCapabilities
(
    DatabaseName     sysname         NULL,
    DatabaseUser     sysname         NULL,
    role_sys         nvarchar(150)   NULL,
    [grant]          nvarchar(128)   NULL,
    execute_details  nvarchar(max)   NULL
);
DECLARE @DatabaseName sysname;
DECLARE @Sql nvarchar(max);
DECLARE @LoginLiteral nvarchar(258)
    = QUOTENAME(@LoginName, NCHAR(39));

--------------------------------------------------------------
-- 遍历所有 Online Database

--------------------------------------------------------------
DECLARE database_cursor CURSOR
LOCAL FAST_FORWARD
FOR
SELECT [name]
FROM sys.databases
WHERE state_desc = N'ONLINE'
  AND HAS_DBACCESS([name]) = 1
ORDER BY [name];
OPEN database_cursor;
FETCH NEXT FROM database_cursor
INTO @DatabaseName;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @Sql =
    N'
USE ' + QUOTENAME(@DatabaseName) + N';
SET NOCOUNT ON;
DECLARE @DatabaseUser sysname = NULL;
DECLARE @CanAdmin bit = 0;
DECLARE @CanDdl bit = 0;
DECLARE @CanExecuteDatabase bit = 0;
DECLARE @CanWrite bit = 0;
DECLARE @CanRead bit = 0;
DECLARE @CanViewDefinition bit = 0;
DECLARE @CanViewDefinitionDatabase bit = 0;
DECLARE @RoleSummary nvarchar(150) = NULL;
DECLARE @GrantSummary nvarchar(128) = NULL;
DECLARE @ExecuteDetails nvarchar(max) = NULL;
DECLARE @IsImpersonated bit = 0;

--------------------------------------------------------------
-- 1. 找 Login SID 真正映射的 Database User

--------------------------------------------------------------
SELECT TOP (1)
    @DatabaseUser = dp.[name]
FROM sys.database_principals AS dp
WHERE dp.[sid] = @LoginSid
  AND dp.principal_id > 0
  AND dp.[type] IN
      (
          N''S'',   -- SQL User
          N''U'',   -- Windows User
          N''G'',   -- Windows Group
          N''E'',   -- External User
          N''X''    -- External Group
      )
ORDER BY dp.principal_id;
BEGIN TRY
    EXECUTE AS LOGIN = ' + @LoginLiteral + N';
    SET @IsImpersonated = 1;

    /*==========================================================
      2. ADMIN
      CONTROL DATABASE
    ==========================================================*/
    SET @CanAdmin =
        COALESCE
        (
            HAS_PERMS_BY_NAME
            (
                DB_NAME(),
                N''DATABASE'',
                N''CONTROL''
            ),
            0
        );

    /*==========================================================
      3. EXECUTOR
      DATABASE 范围 EXECUTE
    ==========================================================*/
    SET @CanExecuteDatabase =
        COALESCE
        (
            HAS_PERMS_BY_NAME
            (
                DB_NAME(),
                N''DATABASE'',
                N''EXECUTE''
            ),
            0
        );

    /*==========================================================
      4. WRITER
      INSERT / UPDATE / DELETE 任一成立
    ==========================================================*/
    ----------------------------------------------------------
    -- 4-A. Database Level
    ----------------------------------------------------------
    IF
        COALESCE
        (
            HAS_PERMS_BY_NAME
            (
                DB_NAME(),
                N''DATABASE'',
                N''INSERT''
            ),
            0
        ) = 1
        OR
        COALESCE
        (
            HAS_PERMS_BY_NAME
            (
                DB_NAME(),
                N''DATABASE'',
                N''UPDATE''
            ),
            0
        ) = 1
        OR
        COALESCE
        (
            HAS_PERMS_BY_NAME
            (
                DB_NAME(),
                N''DATABASE'',
                N''DELETE''
            ),
            0
        ) = 1
    BEGIN
        SET @CanWrite = 1;
    END;
    ----------------------------------------------------------
    -- 4-B. Table / View Effective Permission
    ----------------------------------------------------------
    IF @CanWrite = 0
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s
                ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.[type] IN
                  (
                      N''U'',     -- Table
                      N''V''      -- View
                  )
              AND
              (
                    COALESCE
                    (
                        HAS_PERMS_BY_NAME
                        (
                            QUOTENAME
                            (
                                s.[name]
                                    COLLATE DATABASE_DEFAULT
                            )
                            +
                            N''.''
                            +
                            QUOTENAME
                            (
                                o.[name]
                                    COLLATE DATABASE_DEFAULT
                            ),
                            N''OBJECT'',
                            N''INSERT''
                        ),
                        0
                    ) = 1
                    OR
                    COALESCE
                    (
                        HAS_PERMS_BY_NAME
                        (
                            QUOTENAME
                            (
                                s.[name]
                                    COLLATE DATABASE_DEFAULT
                            )
                            +
                            N''.''
                            +
                            QUOTENAME
                            (
                                o.[name]
                                    COLLATE DATABASE_DEFAULT
                            ),
                            N''OBJECT'',
                            N''UPDATE''
                        ),
                        0
                    ) = 1
                    OR
                    COALESCE
                    (
                        HAS_PERMS_BY_NAME
                        (
                            QUOTENAME
                            (
                                s.[name]
                                    COLLATE DATABASE_DEFAULT
                            )
                            +
                            N''.''
                            +
                            QUOTENAME
                            (
                                o.[name]
                                    COLLATE DATABASE_DEFAULT
                            ),
                            N''OBJECT'',
                            N''DELETE''
                        ),
                        0
                    ) = 1
              )
        )
        BEGIN
            SET @CanWrite = 1;
        END;
    END;

    /*==========================================================
      5. READER
      SELECT
    ==========================================================*/
    ----------------------------------------------------------
    -- 5-A. Database Level
    ----------------------------------------------------------
    IF
        COALESCE
        (
            HAS_PERMS_BY_NAME
            (
                DB_NAME(),
                N''DATABASE'',
                N''SELECT''
            ),
            0
        ) = 1
    BEGIN
        SET @CanRead = 1;
    END;
    ----------------------------------------------------------
    -- 5-B. Table / View Effective Permission
    ----------------------------------------------------------
    IF @CanRead = 0
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM sys.objects AS o
            INNER JOIN sys.schemas AS s
                ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.[type] IN
                  (
                      N''U'',
                      N''V''
                  )
              AND
                COALESCE
                (
                    HAS_PERMS_BY_NAME
                    (
                        QUOTENAME
                        (
                            s.[name]
                                COLLATE DATABASE_DEFAULT
                        )
                        +
                        N''.''
                        +
                        QUOTENAME
                        (
                            o.[name]
                                COLLATE DATABASE_DEFAULT
                        ),
                        N''OBJECT'',
                        N''SELECT''
                    ),
                    0
                ) = 1
        )
        BEGIN
            SET @CanRead = 1;
        END;
    END;

    /*==========================================================
      6. DDL
      收拢判断：
        - 系统数据库不判断 ddl
        - admin 不重复显示 ddl
        - 只认 DATABASE ALTER / ALTER ANY SCHEMA
      不扫描：
        CREATE TABLE / VIEW / PROCEDURE ...
        Schema ALTER
        Object ALTER
    ==========================================================*/
    SET @CanDdl = 0;
    IF @CanAdmin = 0
       AND DB_ID() > 4
    BEGIN
        IF
            COALESCE
            (
                HAS_PERMS_BY_NAME
                (
                    DB_NAME(),
                    N''DATABASE'',
                    N''ALTER''
                ),
                0
            ) = 1
            OR
            COALESCE
            (
                HAS_PERMS_BY_NAME
                (
                    DB_NAME(),
                    N''DATABASE'',
                    N''ALTER ANY SCHEMA''
                ),
                0
            ) = 1
        BEGIN
            SET @CanDdl = 1;
        END;
    END;

    /*==========================================================
      7. VIEW DEFINITION
    ==========================================================*/
    SET @CanViewDefinitionDatabase =
        COALESCE
        (
            HAS_PERMS_BY_NAME
            (
                DB_NAME(),
                N''DATABASE'',
                N''VIEW DEFINITION''
            ),
            0
        );
    SET @CanViewDefinition = @CanViewDefinitionDatabase;

    IF @CanViewDefinition = 0
       AND EXISTS
       (
           SELECT 1
           FROM sys.schemas AS s
           WHERE s.[name] NOT IN (N''sys'', N''INFORMATION_SCHEMA'')
             AND COALESCE
                 (
                     HAS_PERMS_BY_NAME
                     (
                         s.[name] COLLATE DATABASE_DEFAULT,
                         N''SCHEMA'',
                         N''VIEW DEFINITION''
                     ),
                     0
                 ) = 1
       )
    BEGIN
        SET @CanViewDefinition = 1;
    END;

    IF @CanViewDefinition = 0
       AND EXISTS
       (
           SELECT 1
           FROM sys.objects AS o
           INNER JOIN sys.schemas AS s
               ON s.schema_id = o.schema_id
           WHERE o.is_ms_shipped = 0
             AND COALESCE
                 (
                     HAS_PERMS_BY_NAME
                     (
                         QUOTENAME(s.[name] COLLATE DATABASE_DEFAULT)
                         + N''.''
                         + QUOTENAME(o.[name] COLLATE DATABASE_DEFAULT),
                         N''OBJECT'',
                         N''VIEW DEFINITION''
                     ),
                     0
                 ) = 1
       )
    BEGIN
        SET @CanViewDefinition = 1;
    END;

    SET @GrantSummary =
        CASE
            WHEN @CanViewDefinitionDatabase = 1
                THEN N''VIEW DEFINITION''
            WHEN @CanViewDefinition = 1
                THEN N''VIEW DEFINITION (PARTIAL)''
            ELSE NULL
        END;

    /*==========================================================
      8. EXECUTE DETAILS
      Database EXECUTE：
          role_sys = executor
          不展开
      Schema EXECUTE：
          DB.schema.* [SCHEMA EXECUTE]
      Specific Procedure：
          DB.schema.proc
      系统数据库：
          不展开
    ==========================================================*/
    IF DB_ID() > 4
       AND @CanExecuteDatabase = 0
    BEGIN
        DECLARE @SchemaExecuteDetails nvarchar(max) = NULL;
        DECLARE @ProcedureExecuteDetails nvarchar(max) = NULL;
        ------------------------------------------------------
        -- 8-A. Schema EXECUTE
        ------------------------------------------------------
        SELECT
            @SchemaExecuteDetails =
                STRING_AGG
                (
                    CAST
                    (
                        x.SchemaExecuteName
                            COLLATE DATABASE_DEFAULT
                        AS nvarchar(max)
                    ),
                    N'', ''
                )
                WITHIN GROUP
                (
                    ORDER BY
                        x.SchemaExecuteName
                            COLLATE DATABASE_DEFAULT
                )
        FROM
        (
            SELECT DISTINCT
                SchemaExecuteName =
                    CONCAT
                    (
                        DB_NAME()
                            COLLATE DATABASE_DEFAULT,
                        N''.'',
                        s.[name]
                            COLLATE DATABASE_DEFAULT,
                        N''.* [SCHEMA EXECUTE]''
                    )
                    COLLATE DATABASE_DEFAULT
            FROM sys.schemas AS s
            WHERE s.[name] NOT IN
                  (
                      N''sys'',
                      N''INFORMATION_SCHEMA''
                  )
              AND EXISTS
                  (
                      SELECT 1
                      FROM sys.procedures AS p
                      WHERE p.schema_id = s.schema_id
                        AND p.is_ms_shipped = 0
                  )
              AND
                COALESCE
                (
                    HAS_PERMS_BY_NAME
                    (
                        s.[name]
                            COLLATE DATABASE_DEFAULT,
                        N''SCHEMA'',
                        N''EXECUTE''
                    ),
                    0
                ) = 1
        ) AS x;
        ------------------------------------------------------
        -- 8-B. Specific Procedure EXECUTE
        ------------------------------------------------------
        SELECT
            @ProcedureExecuteDetails =
                STRING_AGG
                (
                    CAST
                    (
                        x.ProcedureName
                            COLLATE DATABASE_DEFAULT
                        AS nvarchar(max)
                    ),
                    N'', ''
                )
                WITHIN GROUP
                (
                    ORDER BY
                        x.ProcedureName
                            COLLATE DATABASE_DEFAULT
                )
        FROM
        (
            SELECT DISTINCT
                ProcedureName =
                    CONCAT
                    (
                        DB_NAME()
                            COLLATE DATABASE_DEFAULT,
                        N''.'',
                        p.SchemaName
                            COLLATE DATABASE_DEFAULT,
                        N''.'',
                        p.ProcedureName
                            COLLATE DATABASE_DEFAULT
                    )
                    COLLATE DATABASE_DEFAULT
            FROM
            (
                SELECT
                    po.object_id,
                    SchemaName =
                        s.[name]
                            COLLATE DATABASE_DEFAULT,
                    ProcedureName =
                        po.[name]
                            COLLATE DATABASE_DEFAULT
                FROM sys.procedures AS po
                INNER JOIN sys.schemas AS s
                    ON s.schema_id = po.schema_id
                WHERE po.is_ms_shipped = 0
                  ------------------------------------------------
                  -- 排除 Database Diagram SP
                  ------------------------------------------------
                  AND NOT
                  (
                      s.[name]
                          COLLATE DATABASE_DEFAULT
                          =
                      N''dbo''
                          COLLATE DATABASE_DEFAULT
                      AND
                      po.[name]
                          COLLATE DATABASE_DEFAULT
                          IN
                      (
                          N''sp_alterdiagram''
                              COLLATE DATABASE_DEFAULT,
                          N''sp_creatediagram''
                              COLLATE DATABASE_DEFAULT,
                          N''sp_dropdiagram''
                              COLLATE DATABASE_DEFAULT,
                          N''sp_helpdiagramdefinition''
                              COLLATE DATABASE_DEFAULT,
                          N''sp_helpdiagrams''
                              COLLATE DATABASE_DEFAULT,
                          N''sp_renamediagram''
                              COLLATE DATABASE_DEFAULT,
                          N''sp_upgraddiagrams''
                              COLLATE DATABASE_DEFAULT
                      )
                  )
            ) AS p
            WHERE
                --------------------------------------------------
                -- 最终能够执行 Procedure
                --------------------------------------------------
                COALESCE
                (
                    HAS_PERMS_BY_NAME
                    (
                        QUOTENAME
                        (
                            p.SchemaName
                                COLLATE DATABASE_DEFAULT
                        )
                        +
                        N''.''
                        +
                        QUOTENAME
                        (
                            p.ProcedureName
                                COLLATE DATABASE_DEFAULT
                        ),
                        N''OBJECT'',
                        N''EXECUTE''
                    ),
                    0
                ) = 1
                --------------------------------------------------
                -- Schema 已有 EXECUTE 时不逐个展开
                --------------------------------------------------
                AND
                COALESCE
                (
                    HAS_PERMS_BY_NAME
                    (
                        p.SchemaName
                            COLLATE DATABASE_DEFAULT,
                        N''SCHEMA'',
                        N''EXECUTE''
                    ),
                    0
                ) = 0
        ) AS x;
        ------------------------------------------------------
        -- 合并 Schema / Procedure
        ------------------------------------------------------
        SET @ExecuteDetails =
            CASE
                WHEN @SchemaExecuteDetails IS NOT NULL
                 AND @ProcedureExecuteDetails IS NOT NULL
                    THEN
                        @SchemaExecuteDetails
                        +
                        N'', ''
                        +
                        @ProcedureExecuteDetails
                WHEN @SchemaExecuteDetails IS NOT NULL
                    THEN @SchemaExecuteDetails
                WHEN @ProcedureExecuteDetails IS NOT NULL
                    THEN @ProcedureExecuteDetails
                ELSE NULL
            END;
    END;

    /*==========================================================
      9. ROLE SUMMARY
      固定顺序：admin | ddl | executor | writer | reader
      reader + execute_details：reader*
    ==========================================================*/
    SET @RoleSummary = CONCAT_WS
    (
        N'' | '',
        CASE WHEN @CanAdmin = 1 THEN N''admin'' END,
        CASE WHEN @CanDdl = 1 AND @CanAdmin = 0 THEN N''ddl'' END,
        CASE WHEN @CanExecuteDatabase = 1 THEN N''executor'' END,
        CASE WHEN @CanWrite = 1 THEN N''writer'' END,
        CASE WHEN @CanRead = 1
             THEN CASE WHEN @ExecuteDetails IS NOT NULL THEN N''reader*'' ELSE N''reader'' END
        END
    );
    IF LEN(@RoleSummary) = 0
        SET @RoleSummary = NULL;
    ----------------------------------------------------------
    -- 10. REVERT
    ----------------------------------------------------------
    REVERT;
    SET @IsImpersonated = 0;
END TRY
BEGIN CATCH
    IF @IsImpersonated = 1
    BEGIN
        BEGIN TRY
            REVERT;
        END TRY
        BEGIN CATCH
        END CATCH;
    END;
    THROW;
END CATCH;

--------------------------------------------------------------
-- 写入结果

--------------------------------------------------------------
INSERT INTO #DatabaseCapabilities
(
    DatabaseName,
    DatabaseUser,
    role_sys,
    [grant],
    execute_details
)
VALUES
(
    DB_NAME(),
    @DatabaseUser,
    @RoleSummary,
    @GrantSummary,
    @ExecuteDetails
);
';
    ----------------------------------------------------------
    -- 执行当前 Database
    ----------------------------------------------------------
    BEGIN TRY
        EXEC sys.sp_executesql
            @Sql,
            N'@LoginSid varbinary(85)',
            @LoginSid = @LoginSid;
    END TRY
    BEGIN CATCH
        PRINT
        (
            N'Database [' +
            @DatabaseName +
            N'] 检查失败。Error ' +
            CONVERT(nvarchar(20), ERROR_NUMBER()) +
            N'，Line ' +
            CONVERT(nvarchar(20), ERROR_LINE()) +
            N'：' +
            ERROR_MESSAGE()
        );
        INSERT INTO #DatabaseCapabilities
        (
            DatabaseName,
            DatabaseUser,
            role_sys,
            [grant],
            execute_details
        )
        VALUES
        (
            @DatabaseName,
            NULL,
            NULL,
            NULL,
            NULL
        );
    END CATCH;
    FETCH NEXT FROM database_cursor
    INTO @DatabaseName;
END;
CLOSE database_cursor;
DEALLOCATE database_cursor;

--------------------------------------------------------------
-- 最终结果

--------------------------------------------------------------
SELECT
    DatabaseName,
    DatabaseUser,
    role_sys,
    [grant],
    execute_details
FROM #DatabaseCapabilities
WHERE DatabaseUser IS NOT NULL
   OR role_sys IS NOT NULL
   OR [grant] IS NOT NULL
   OR execute_details IS NOT NULL
ORDER BY DatabaseName;
