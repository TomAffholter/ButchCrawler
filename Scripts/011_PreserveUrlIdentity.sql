-- Run against your crawler database with all crawler/operator instances stopped.
-- Back up first. Changes and index rebuilds are atomic. Re-running is harmless.
-- URL paths and query strings are case-sensitive; host normalization happens in C#.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

BEGIN TRY
    BEGIN TRANSACTION;
    CREATE TABLE #UrlColumns (ObjectId INT, ColumnId INT, TableName NVARCHAR(517), ColumnName SYSNAME,
        TypeName SYSNAME, MaxLength SMALLINT, IsNullable BIT);
    INSERT #UrlColumns
    SELECT t.object_id, c.column_id, QUOTENAME(s.name) + N'.' + QUOTENAME(t.name), c.name,
           ty.name, c.max_length, c.is_nullable
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.columns c ON c.object_id = t.object_id
    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
    WHERE s.name = N'dbo'
      AND t.name IN (N'SeedUrls', N'CrawlQueue', N'CrawlResults', N'VisitedUrls', N'documents',
                     N'PdfResults', N'CrawlArtifacts', N'CrawlCaptures', N'ContentDiffs', N'CaptchaTasks', N'CrawlKeywordHits')
      AND c.name IN (N'Url', N'SeedUrl', N'ParentUrl', N'FinalUrl')
      AND c.collation_name IS NOT NULL
      AND (c.collation_name <> N'Latin1_General_100_BIN2'
           OR (t.name=N'CrawlQueue' AND EXISTS (SELECT 1 FROM sys.computed_columns cc
               WHERE cc.object_id=t.object_id AND cc.name IN (N'UrlHash',N'SeedUrlHash') AND cc.definition LIKE N'%upper%')));

    -- Older queue migrations uppercased URLs before hashing. Rebuild those computed
    -- columns and their indexes as well, otherwise distinct paths still collide.
    CREATE TABLE #Hashes (ObjectId INT, ColumnId INT, TableName NVARCHAR(517), ColumnName SYSNAME, CreateSql NVARCHAR(MAX));
    INSERT #Hashes
    SELECT cc.object_id, cc.column_id, N'[dbo].[CrawlQueue]', cc.name,
        N'ALTER TABLE [dbo].[CrawlQueue] ADD '+QUOTENAME(cc.name)
        +N' AS CONVERT(VARBINARY(32), HASHBYTES(''SHA2_256'', CONVERT(NVARCHAR(4000), ISNULL('
        +CASE WHEN cc.name=N'UrlHash' THEN N'[Url]' ELSE N'[SeedUrl]' END+N', N''''))))'
        +CASE WHEN cc.is_persisted=1 THEN N' PERSISTED' ELSE N'' END
    FROM sys.computed_columns cc
    WHERE cc.object_id=OBJECT_ID(N'dbo.CrawlQueue') AND cc.name IN (N'UrlHash',N'SeedUrlHash')
      AND EXISTS (SELECT 1 FROM #UrlColumns c WHERE c.ObjectId=cc.object_id);
    SELECT ObjectId,ColumnId,TableName INTO #Affected FROM #UrlColumns
    UNION SELECT ObjectId,ColumnId,TableName FROM #Hashes;

    -- Custom schema dependencies require a tailored migration, never silently remove them.
    IF EXISTS (SELECT 1 FROM #UrlColumns WHERE TypeName NOT IN (N'nvarchar', N'varchar', N'nchar', N'char'))
        THROW 50011, 'URL migration requires standard char/varchar/nchar/nvarchar columns.', 1;
    IF EXISTS (SELECT 1 FROM sys.foreign_key_columns fk JOIN #UrlColumns c
        ON (c.ObjectId = fk.parent_object_id AND c.ColumnId = fk.parent_column_id)
        OR (c.ObjectId = fk.referenced_object_id AND c.ColumnId = fk.referenced_column_id))
        THROW 50011, 'A custom URL foreign key exists. Have the database administrator migrate its collation and both endpoints together.', 1;

    CREATE TABLE #Indexes (ObjectId INT, IndexId INT, DropSql NVARCHAR(MAX), CreateSql NVARCHAR(MAX));
    IF EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
        JOIN #Affected c ON c.ObjectId=ic.object_id AND c.ColumnId=ic.column_id
        JOIN sys.data_spaces ds ON ds.data_space_id=i.data_space_id
        WHERE i.type NOT IN (1,2) OR ds.type <> N'FG' OR i.is_disabled=1 OR i.is_hypothetical=1)
        THROW 50011, 'A custom, disabled or partitioned URL index exists. A tailored migration is required.', 1;
    IF EXISTS (SELECT 1 FROM sys.partitions p JOIN sys.index_columns ic ON ic.object_id=p.object_id AND ic.index_id=p.index_id
        JOIN #Affected c ON c.ObjectId=ic.object_id AND c.ColumnId=ic.column_id WHERE p.data_compression <> 0)
        THROW 50011, 'A compressed URL index exists. A tailored migration is required.', 1;

    DECLARE @object INT, @index INT, @table NVARCHAR(517), @name SYSNAME, @type INT,
        @unique BIT, @pk BIT, @uq BIT, @filter NVARCHAR(MAX), @space SYSNAME,
        @keys NVARCHAR(MAX), @includes NVARCHAR(MAX), @options NVARCHAR(MAX), @sql NVARCHAR(MAX);
    DECLARE index_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT DISTINCT i.object_id, i.index_id, c.TableName, i.name, i.type, i.is_unique, i.is_primary_key,
        i.is_unique_constraint, i.filter_definition, ds.name,
        N'PAD_INDEX = ' + CASE WHEN i.is_padded=1 THEN N'ON' ELSE N'OFF' END
        + N', FILLFACTOR = ' + CONVERT(NVARCHAR(3), CASE WHEN i.fill_factor=0 THEN 100 ELSE i.fill_factor END)
        + N', IGNORE_DUP_KEY = ' + CASE WHEN i.ignore_dup_key=1 THEN N'ON' ELSE N'OFF' END
        + N', ALLOW_ROW_LOCKS = ' + CASE WHEN i.allow_row_locks=1 THEN N'ON' ELSE N'OFF' END
        + N', ALLOW_PAGE_LOCKS = ' + CASE WHEN i.allow_page_locks=1 THEN N'ON' ELSE N'OFF' END
    FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
    JOIN #Affected c ON c.ObjectId=ic.object_id AND c.ColumnId=ic.column_id
    JOIN sys.data_spaces ds ON ds.data_space_id=i.data_space_id;
    OPEN index_cursor;
    FETCH NEXT FROM index_cursor INTO @object,@index,@table,@name,@type,@unique,@pk,@uq,@filter,@space,@options;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @keys = STRING_AGG(CONVERT(NVARCHAR(MAX), QUOTENAME(c.name)
            + CASE WHEN ic.is_descending_key=1 THEN N' DESC' ELSE N' ASC' END), N', ')
            WITHIN GROUP (ORDER BY ic.key_ordinal)
        FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
        WHERE ic.object_id=@object AND ic.index_id=@index AND ic.key_ordinal>0;
        SELECT @includes = STRING_AGG(CONVERT(NVARCHAR(MAX), QUOTENAME(c.name)), N', ')
            WITHIN GROUP (ORDER BY ic.index_column_id)
        FROM sys.index_columns ic JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
        WHERE ic.object_id=@object AND ic.index_id=@index AND ic.is_included_column=1;
        INSERT #Indexes VALUES (@object,@index,
            CASE WHEN @pk=1 OR @uq=1 THEN N'ALTER TABLE '+@table+N' DROP CONSTRAINT '+QUOTENAME(@name)
                 ELSE N'DROP INDEX '+QUOTENAME(@name)+N' ON '+@table END,
            CASE WHEN @pk=1 OR @uq=1 THEN N'ALTER TABLE '+@table+N' ADD CONSTRAINT '+QUOTENAME(@name)
                    +CASE WHEN @pk=1 THEN N' PRIMARY KEY ' ELSE N' UNIQUE ' END
                 ELSE N'CREATE '+CASE WHEN @unique=1 THEN N'UNIQUE ' ELSE N'' END END
            + CASE WHEN @type=1 THEN N'CLUSTERED ' ELSE N'NONCLUSTERED ' END
            + CASE WHEN @pk=1 OR @uq=1 THEN N'' ELSE N'INDEX '+QUOTENAME(@name)+N' ON '+@table END
            + N' ('+@keys+N')'
            + CASE WHEN @includes IS NOT NULL THEN N' INCLUDE ('+@includes+N')' ELSE N'' END
            + CASE WHEN @filter IS NOT NULL THEN N' WHERE '+@filter ELSE N'' END
            + N' WITH ('+@options+N') ON '+QUOTENAME(@space));
        FETCH NEXT FROM index_cursor INTO @object,@index,@table,@name,@type,@unique,@pk,@uq,@filter,@space,@options;
    END;
    CLOSE index_cursor; DEALLOCATE index_cursor;

    DECLARE drop_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT DropSql FROM #Indexes ORDER BY ObjectId, IndexId DESC;
    OPEN drop_cursor; FETCH NEXT FROM drop_cursor INTO @sql;
    WHILE @@FETCH_STATUS=0 BEGIN EXEC sp_executesql @sql; FETCH NEXT FROM drop_cursor INTO @sql; END;
    CLOSE drop_cursor; DEALLOCATE drop_cursor;

    DECLARE hash_drop_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT N'ALTER TABLE '+TableName+N' DROP COLUMN '+QUOTENAME(ColumnName) FROM #Hashes;
    OPEN hash_drop_cursor; FETCH NEXT FROM hash_drop_cursor INTO @sql;
    WHILE @@FETCH_STATUS=0 BEGIN EXEC sp_executesql @sql; FETCH NEXT FROM hash_drop_cursor INTO @sql; END;
    CLOSE hash_drop_cursor; DEALLOCATE hash_drop_cursor;

    DECLARE column_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT N'ALTER TABLE '+TableName+N' ALTER COLUMN '+QUOTENAME(ColumnName)+N' '+TypeName+N'('
        + CASE WHEN MaxLength=-1 THEN N'MAX' ELSE CONVERT(NVARCHAR(10),MaxLength/CASE WHEN TypeName IN (N'nvarchar',N'nchar') THEN 2 ELSE 1 END) END
        + N') COLLATE Latin1_General_100_BIN2 '+CASE WHEN IsNullable=1 THEN N'NULL' ELSE N'NOT NULL' END FROM #UrlColumns;
    OPEN column_cursor; FETCH NEXT FROM column_cursor INTO @sql;
    WHILE @@FETCH_STATUS=0 BEGIN EXEC sp_executesql @sql; FETCH NEXT FROM column_cursor INTO @sql; END;
    CLOSE column_cursor; DEALLOCATE column_cursor;

    DECLARE hash_create_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT CreateSql FROM #Hashes;
    OPEN hash_create_cursor; FETCH NEXT FROM hash_create_cursor INTO @sql;
    WHILE @@FETCH_STATUS=0 BEGIN EXEC sp_executesql @sql; FETCH NEXT FROM hash_create_cursor INTO @sql; END;
    CLOSE hash_create_cursor; DEALLOCATE hash_create_cursor;

    DECLARE create_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT CreateSql FROM #Indexes ORDER BY ObjectId, IndexId;
    OPEN create_cursor; FETCH NEXT FROM create_cursor INTO @sql;
    WHILE @@FETCH_STATUS=0 BEGIN EXEC sp_executesql @sql; FETCH NEXT FROM create_cursor INTO @sql; END;
    CLOSE create_cursor; DEALLOCATE create_cursor;
    DROP TABLE #Indexes; DROP TABLE #Affected; DROP TABLE #Hashes; DROP TABLE #UrlColumns;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
