/* 管理员在配置库手工执行；不自动部署函数或修改权限。
   首次仅新增可空 summary；人工补齐后重跑完成 NOT NULL。
   不从 desp 自动生成摘要。部署最终函数与新版 MCP 后才使用空 desp。
   已部署最终契约的环境无需执行。 */
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.tools_info', N'U') IS NULL
    THROW 50000, N'tools_info 不存在，请使用首次建表脚本。', 1;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.tools_info')
    AND name = N'desp' AND system_type_id = 231 AND max_length = -1 AND is_nullable = 0)
    THROW 50000, N'desp 必须为 nvarchar(max) NOT NULL，请管理员核对。', 1;
IF COL_LENGTH(N'dbo.tools_info', N'summary') IS NULL
    EXEC sys.sp_executesql N'ALTER TABLE dbo.tools_info ADD summary nvarchar(1000) NULL;';
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.tools_info')
    AND name = N'summary' AND system_type_id = 231 AND max_length = 2000)
    THROW 50000, N'summary 类型应为 nvarchar(1000)，请管理员核对。', 1;
-- 仅核对 summary 的 .NET 空白字符；不改变 desp 或其 has_desp 计算。
DECLARE @missing int;
EXEC sys.sp_executesql N'
    DECLARE @invalid TABLE (id int);
    DECLARE @id int, @summary nvarchar(1000), @pos int, @n int, @blank bit;
    DECLARE summaries CURSOR LOCAL FAST_FORWARD FOR SELECT id, summary FROM dbo.tools_info;
    OPEN summaries;
    FETCH NEXT FROM summaries INTO @id, @summary;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @pos = 1;
        SET @blank = 1;
        WHILE @pos <= DATALENGTH(@summary) / 2
        BEGIN
            SET @n = UNICODE(SUBSTRING(@summary, @pos, 1));
            IF @n NOT IN (9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)
            BEGIN SET @blank = 0; BREAK; END;
            SET @pos += 1;
        END;
        IF @blank = 1 INSERT INTO @invalid VALUES (@id);
        FETCH NEXT FROM summaries INTO @id, @summary;
    END;
    CLOSE summaries;
    DEALLOCATE summaries;
    SELECT @count = COUNT(*) FROM @invalid;
    SELECT id AS summary_required_id FROM @invalid ORDER BY id;',
    N'@count int OUTPUT', @count = @missing OUTPUT;
IF @missing > 0
    THROW 50000, N'请人工补齐上述 id 的 summary 后重跑；本脚本未改写任何正文。', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.tools_info') AND name = N'summary' AND is_nullable = 1)
    EXEC sys.sp_executesql N'ALTER TABLE dbo.tools_info ALTER COLUMN summary nvarchar(1000) NOT NULL;';
PRINT N'摘要结构检查完成。请在切换窗口部署最终函数及新版 MCP。';
