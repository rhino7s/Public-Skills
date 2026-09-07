/*==============================================================
  SQL Server 最终权限能力检查
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
    - @CheckMode = currentSession：默认；必须由实际 MCP Windows/AD 身份直接连接并运行，不执行身份模拟
    - @CheckMode = impersonateLogin：只适用于单一 SQL Login 或 Windows 用户；将 <readonly_login> 替换为目标，执行者须有 IMPERSONATE 权限
    - @DatabaseFilter = NULL：检查所有可访问数据库；也可填写一个数据库名称，缩小检查范围
    - AD 群组不能作为 EXECUTE AS LOGIN 目标；群组成员的最终权限必须使用 currentSession 检查
    - 不检查原始 Role 名称，只看最终有效权限
    - 不扫描 Column-level 权限
    - 不把 guest 当 DatabaseUser
    - 系统数据库不判断 ddl
    - 系统数据库不展开 execute_details
/*
    USE [ExampleDatabase];

    SELECT
        ORIGINAL_LOGIN() AS LoginName,
        USER_NAME() AS DatabaseUser;

    -- Database 层级
    SELECT *
    FROM fn_my_permissions(NULL, 'DATABASE')
    ORDER BY permission_name;

    -- Table 层级
    SELECT *
    FROM fn_my_permissions('dbo.ExampleTable', 'OBJECT')
    ORDER BY permission_name;

*/
==============================================================*/
DECLARE @CheckMode nvarchar(32) = N'currentSession';
DECLARE @LoginName sysname = N'<readonly_login>';
DECLARE @DatabaseFilter sysname = NULL;
SET @DatabaseFilter = COALESCE
(
    @DatabaseFilter,
    TRY_CONVERT(sysname, SESSION_CONTEXT(N'sqlserver_readonly_mcp.accessCheckDatabase'))
);
IF @CheckMode NOT IN (N'currentSession', N'impersonateLogin')
    THROW 50000, N'@CheckMode 只允许 currentSession 或 impersonateLogin。', 1;
IF @CheckMode = N'impersonateLogin'
   AND SUSER_SID(@LoginName) IS NULL
    THROW 50001, N'找不到指定的 SQL Server Login。', 1;
IF @CheckMode = N'currentSession'
    SET @LoginName = ORIGINAL_LOGIN();
IF @DatabaseFilter IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1
       FROM sys.databases
       WHERE [name] = @DatabaseFilter
         AND state_desc = N'ONLINE'
         AND HAS_DBACCESS([name]) = 1
   )
    THROW 50002, N'@DatabaseFilter 指定的数据库不存在、未联机或当前身份无权访问。', 1;
DROP TABLE IF EXISTS #DatabaseCapabilities;
CREATE TABLE #DatabaseCapabilities
(
    DatabaseName     sysname         NULL,
    DatabaseUser     sysname         NULL,
    role_sys         nvarchar(150)   NULL,
    [grant]          nvarchar(128)   NULL,
    execute_details  nvarchar(max)   NULL,
    check_error      nvarchar(2048)  NULL
);
DECLARE @DatabaseName sysname;
DECLARE @Sql nvarchar(max);

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
  AND (@DatabaseFilter IS NULL OR [name] = @DatabaseFilter)
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
-- 1. 建立实际检查身份

--------------------------------------------------------------
BEGIN TRY
    IF @CheckMode = N''impersonateLogin''
    BEGIN
        EXECUTE AS LOGIN = @TargetLoginName;
        SET @IsImpersonated = 1;
    END;

    SET @DatabaseUser =
        CASE
            WHEN USER_NAME() = N''guest'' THEN NULL
            ELSE USER_NAME()
        END;

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
    IF @IsImpersonated = 1
    BEGIN
        REVERT;
        SET @IsImpersonated = 0;
    END;
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
    execute_details,
    check_error
)
VALUES
(
    DB_NAME(),
    @DatabaseUser,
    @RoleSummary,
    @GrantSummary,
    @ExecuteDetails,
    NULL
);
';
    ----------------------------------------------------------
    -- 执行当前 Database
    ----------------------------------------------------------
    BEGIN TRY
        EXEC sys.sp_executesql
            @Sql,
            N'@CheckMode nvarchar(32), @TargetLoginName sysname',
            @CheckMode = @CheckMode,
            @TargetLoginName = @LoginName;
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
            execute_details,
            check_error
        )
        VALUES
        (
            @DatabaseName,
            NULL,
            NULL,
            NULL,
            NULL,
            CONCAT
            (
                N'Error ',
                CONVERT(nvarchar(20), ERROR_NUMBER()),
                N'，Line ',
                CONVERT(nvarchar(20), ERROR_LINE()),
                N'：',
                ERROR_MESSAGE()
            )
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
    @CheckMode AS CheckMode,
    @LoginName AS LoginName;

SELECT
    DatabaseName,
    DatabaseUser,
    role_sys,
    [grant],
    execute_details,
    check_error
FROM #DatabaseCapabilities
WHERE DatabaseUser IS NOT NULL
   OR role_sys IS NOT NULL
   OR [grant] IS NOT NULL
   OR execute_details IS NOT NULL
   OR check_error IS NOT NULL
ORDER BY DatabaseName;
