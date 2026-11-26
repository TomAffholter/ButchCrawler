using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed record ResetTable(string Property, long Rows, bool Preserved);

public sealed record DatabaseResetResult(
    string Database,
    string BackupPath,
    long SeedUrls,
    long SeedKeywords,
    int TablesCleared);

public sealed class DatabaseResetService
{
    public const string ConfirmationPhrase = "START NEW";

    private readonly string _connectionString;
    private readonly string _expectedDatabase;

    public DatabaseResetService(string connectionString, string expectedDatabase = "ButchCrawlerDB")
    {
        _connectionString = connectionString;
        _expectedDatabase = expectedDatabase;
    }

    public static bool IsPreservedTable(string schema, string table, bool clearKeywords) =>
        string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase) &&
        (string.Equals(table, "SeedUrls", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(table, "SchemaVersion", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(table, "HistoryCrawlerSubscriptions", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(table, "HistoryCrawlerSuggestions", StringComparison.OrdinalIgnoreCase) ||
         (!clearKeywords && string.Equals(table, "SeedKeywords", StringComparison.OrdinalIgnoreCase)));

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            if (!string.Equals(connection.Database, _expectedDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"New-project reset is restricted to {_expectedDatabase}.");

            using var check = connection.CreateCommand();
            check.CommandText = @"SELECT CASE WHEN OBJECT_ID(N'dbo.SeedUrls',N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.SeedKeywords',N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.SchemaVersion',N'U') IS NOT NULL THEN 1 ELSE 0 END";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidOperationException("Required Seed URLs, Seed Keywords, or schema-version tables were not found.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<ResetTable>> PreviewAsync(bool clearKeywords, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT s.name,t.name,COALESCE(SUM(p.rows),0)
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
LEFT JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)
WHERE t.is_ms_shipped=0
GROUP BY s.name,t.name ORDER BY s.name,t.name";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ResetTable>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            rows.Add(new ResetTable(schema + "." + table, reader.GetInt64(2), IsPreservedTable(schema, table, clearKeywords)));
        }
        return rows;
    }

    public async Task<DatabaseResetResult> StartNewResearchProjectAsync(bool clearKeywords, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureNoActiveCrawlerAsync(connection, cancellationToken);
        var backupPath = await CreateVerifiedBackupAsync(connection, cancellationToken);
        var tablesCleared = await ClearResearchDataAsync(connection, clearKeywords, cancellationToken);
        var (seedUrls, seedKeywords) = await VerifyAfterResetAsync(connection, clearKeywords, cancellationToken);
        return new DatabaseResetResult(connection.Database, backupPath, seedUrls, seedKeywords, tablesCleared);
    }

    // Retained for existing callers and integration tests.
    public async Task ResetAsync(bool clearKeywords) =>
        _ = await StartNewResearchProjectAsync(clearKeywords);

    private static async Task EnsureNoActiveCrawlerAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"IF OBJECT_ID(N'dbo.CrawlQueue',N'U') IS NOT NULL
 AND COL_LENGTH(N'dbo.CrawlQueue',N'locked_until') IS NOT NULL
 AND COL_LENGTH(N'dbo.CrawlQueue',N'LockToken') IS NOT NULL
 AND EXISTS(SELECT 1 FROM dbo.CrawlQueue WHERE locked_until>SYSUTCDATETIME() AND LockToken IS NOT NULL)
 THROW 51011,'Active crawl work exists. Stop every crawler and wait for active work to finish.',1;
SELECT 1;";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<string> CreateVerifiedBackupAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var backupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HistoryCrawler", "Backups");
        Directory.CreateDirectory(backupFolder);
        var safeDatabase = string.Concat(connection.Database.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var backupPath = Path.Combine(backupFolder,
            $"{safeDatabase}-before-new-project-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak");
        var quotedDatabase = "[" + connection.Database.Replace("]", "]]", StringComparison.Ordinal) + "]";

        using var command = connection.CreateCommand();
        command.CommandTimeout = 1200;
        command.Parameters.Add("@path", SqlDbType.NVarChar, 4000).Value = backupPath;
        command.CommandText = $@"BACKUP DATABASE {quotedDatabase} TO DISK=@path WITH COPY_ONLY,INIT,CHECKSUM;
RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
            throw new InvalidOperationException("The safety backup was not created. No research data was deleted.");
        return backupPath;
    }

    private static async Task<int> ClearResearchDataAsync(SqlConnection connection, bool clearKeywords, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1200;
        command.Parameters.Add("@clearKeywords", SqlDbType.Bit).Value = clearKeywords;
        command.CommandText = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 DECLARE @lockResult INT;
 EXEC @lockResult=sys.sp_getapplock @Resource=N'HistoryCrawler.NewResearchProject',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
 IF @lockResult<0 THROW 51010,'Another new-project reset is already running.',1;

 IF OBJECT_ID(N'dbo.CrawlQueue',N'U') IS NOT NULL
  AND COL_LENGTH(N'dbo.CrawlQueue',N'locked_until') IS NOT NULL
  AND COL_LENGTH(N'dbo.CrawlQueue',N'LockToken') IS NOT NULL
  AND EXISTS(SELECT 1 FROM dbo.CrawlQueue WHERE locked_until>SYSUTCDATETIME() AND LockToken IS NOT NULL)
  THROW 51011,'Active crawl work exists. Stop every crawler and wait for active work to finish.',1;

 DECLARE @seedUrlCount BIGINT=(SELECT COUNT_BIG(*) FROM dbo.SeedUrls);
 DECLARE @seedKeywordCount BIGINT=(SELECT COUNT_BIG(*) FROM dbo.SeedKeywords);
 DECLARE @schemaVersionCount BIGINT=(SELECT COUNT_BIG(*) FROM dbo.SchemaVersion);
 DECLARE @subscriptionCount BIGINT=0,@suggestionCount BIGINT=0;
 IF OBJECT_ID(N'dbo.HistoryCrawlerSubscriptions',N'U') IS NOT NULL EXEC sys.sp_executesql N'SELECT @value=COUNT_BIG(*) FROM dbo.HistoryCrawlerSubscriptions',N'@value bigint OUTPUT',@subscriptionCount OUTPUT;
 IF OBJECT_ID(N'dbo.HistoryCrawlerSuggestions',N'U') IS NOT NULL EXEC sys.sp_executesql N'SELECT @value=COUNT_BIG(*) FROM dbo.HistoryCrawlerSuggestions',N'@value bigint OUTPUT',@suggestionCount OUTPUT;

 DECLARE @tables TABLE(Qualified NVARCHAR(517) PRIMARY KEY,HasIdentity BIT,ReseedValue NVARCHAR(100));
 INSERT @tables(Qualified,HasIdentity,ReseedValue)
 SELECT QUOTENAME(s.name)+N'.'+QUOTENAME(t.name),
        CASE WHEN i.object_id IS NULL THEN 0 ELSE 1 END,
        CASE WHEN i.object_id IS NULL THEN NULL ELSE CONVERT(NVARCHAR(100),CONVERT(DECIMAL(38,0),i.seed_value)-CONVERT(DECIMAL(38,0),i.increment_value)) END
 FROM sys.tables t
 JOIN sys.schemas s ON s.schema_id=t.schema_id
 LEFT JOIN sys.identity_columns i ON i.object_id=t.object_id
 WHERE t.is_ms_shipped=0
   AND NOT(s.name=N'dbo' AND (t.name=N'SeedUrls' OR t.name=N'SchemaVersion'
       OR t.name=N'HistoryCrawlerSubscriptions' OR t.name=N'HistoryCrawlerSuggestions'
       OR (t.name=N'SeedKeywords' AND @clearKeywords=0)));

 DECLARE @constraints TABLE(Qualified NVARCHAR(517),ConstraintName SYSNAME,WasUntrusted BIT);
 INSERT @constraints
 SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+N'.'+QUOTENAME(OBJECT_NAME(parent_object_id)),name,is_not_trusted
 FROM sys.foreign_keys WHERE is_disabled=0
 UNION ALL
 SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+N'.'+QUOTENAME(OBJECT_NAME(parent_object_id)),name,is_not_trusted
 FROM sys.check_constraints WHERE is_disabled=0;

 DECLARE @triggers TABLE(Qualified NVARCHAR(517),TriggerName SYSNAME);
 INSERT @triggers
 SELECT QUOTENAME(OBJECT_SCHEMA_NAME(tr.parent_id))+N'.'+QUOTENAME(OBJECT_NAME(tr.parent_id)),tr.name
 FROM sys.triggers tr JOIN sys.tables t ON t.object_id=tr.parent_id
 WHERE tr.parent_class=1 AND tr.is_disabled=0 AND tr.is_ms_shipped=0;

 DECLARE @qualified NVARCHAR(517),@name SYSNAME,@untrusted BIT,@sql NVARCHAR(MAX),@identity BIT,@reseed NVARCHAR(100),@tablesCleared INT=0;
 DECLARE constraint_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified,ConstraintName FROM @constraints;
 OPEN constraint_cursor; FETCH NEXT FROM constraint_cursor INTO @qualified,@name;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'ALTER TABLE '+@qualified+N' NOCHECK CONSTRAINT '+QUOTENAME(@name)+N';'; EXEC sys.sp_executesql @sql;
  FETCH NEXT FROM constraint_cursor INTO @qualified,@name;
 END
 CLOSE constraint_cursor; DEALLOCATE constraint_cursor;

 DECLARE trigger_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified,TriggerName FROM @triggers;
 OPEN trigger_cursor; FETCH NEXT FROM trigger_cursor INTO @qualified,@name;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'DISABLE TRIGGER '+QUOTENAME(@name)+N' ON '+@qualified+N';'; EXEC sys.sp_executesql @sql;
  FETCH NEXT FROM trigger_cursor INTO @qualified,@name;
 END
 CLOSE trigger_cursor; DEALLOCATE trigger_cursor;

 DECLARE table_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified,HasIdentity,ReseedValue FROM @tables;
 OPEN table_cursor; FETCH NEXT FROM table_cursor INTO @qualified,@identity,@reseed;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'DELETE FROM '+@qualified+N';';
  IF @identity=1 SET @sql+=N'DBCC CHECKIDENT('''+REPLACE(@qualified,'''','''''')+N''',RESEED,'+@reseed+N') WITH NO_INFOMSGS;';
  EXEC sys.sp_executesql @sql; SET @tablesCleared+=1;
  FETCH NEXT FROM table_cursor INTO @qualified,@identity,@reseed;
 END
 CLOSE table_cursor; DEALLOCATE table_cursor;

 IF COL_LENGTH(N'dbo.SeedUrls',N'AutoDisabled') IS NOT NULL
  AND COL_LENGTH(N'dbo.SeedUrls',N'IsActive') IS NOT NULL
  AND COL_LENGTH(N'dbo.SeedUrls',N'DisabledReason') IS NOT NULL
  EXEC(N'UPDATE dbo.SeedUrls SET IsActive=CASE WHEN AutoDisabled=1 THEN 1 ELSE IsActive END,DisabledReason=CASE WHEN AutoDisabled=1 THEN NULL ELSE DisabledReason END,AutoDisabled=0');
 IF COL_LENGTH(N'dbo.SeedUrls',N'LastCrawledAt') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET LastCrawledAt=NULL');
 IF COL_LENGTH(N'dbo.SeedUrls',N'HealthStatus') IS NOT NULL AND COL_LENGTH(N'dbo.SeedUrls',N'IsActive') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET HealthStatus=CASE WHEN IsActive=1 THEN N''healthy'' ELSE N''disabled'' END');
 IF COL_LENGTH(N'dbo.SeedUrls',N'ConsecutiveFailures') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET ConsecutiveFailures=0');
 IF COL_LENGTH(N'dbo.SeedUrls',N'FirstFailureAt') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET FirstFailureAt=NULL');
 IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureAt') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET LastFailureAt=NULL');
 IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureStatus') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET LastFailureStatus=NULL');
 IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureReason') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET LastFailureReason=NULL');
 IF COL_LENGTH(N'dbo.SeedUrls',N'NextHealthCheckAt') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET NextHealthCheckAt=NULL');

 DECLARE restore_constraint_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified,ConstraintName,WasUntrusted FROM @constraints;
 OPEN restore_constraint_cursor; FETCH NEXT FROM restore_constraint_cursor INTO @qualified,@name,@untrusted;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'ALTER TABLE '+@qualified+N' '+CASE WHEN @untrusted=0 THEN N'WITH CHECK CHECK' ELSE N'CHECK' END+N' CONSTRAINT '+QUOTENAME(@name)+N';';
  EXEC sys.sp_executesql @sql; FETCH NEXT FROM restore_constraint_cursor INTO @qualified,@name,@untrusted;
 END
 CLOSE restore_constraint_cursor; DEALLOCATE restore_constraint_cursor;

 DECLARE restore_trigger_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified,TriggerName FROM @triggers;
 OPEN restore_trigger_cursor; FETCH NEXT FROM restore_trigger_cursor INTO @qualified,@name;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'ENABLE TRIGGER '+QUOTENAME(@name)+N' ON '+@qualified+N';'; EXEC sys.sp_executesql @sql;
  FETCH NEXT FROM restore_trigger_cursor INTO @qualified,@name;
 END
 CLOSE restore_trigger_cursor; DEALLOCATE restore_trigger_cursor;

 DECLARE verify_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified FROM @tables;
 OPEN verify_cursor; FETCH NEXT FROM verify_cursor INTO @qualified;
 WHILE @@FETCH_STATUS=0
 BEGIN
  DECLARE @remaining BIGINT=0;
  SET @sql=N'SELECT @value=COUNT_BIG(*) FROM '+@qualified;
  EXEC sys.sp_executesql @sql,N'@value BIGINT OUTPUT',@remaining OUTPUT;
  IF @remaining<>0 BEGIN CLOSE verify_cursor; DEALLOCATE verify_cursor; THROW 51012,'A table expected to be empty still contains data.',1; END;
  FETCH NEXT FROM verify_cursor INTO @qualified;
 END
 CLOSE verify_cursor; DEALLOCATE verify_cursor;

 IF (SELECT COUNT_BIG(*) FROM dbo.SeedUrls)<>@seedUrlCount THROW 51013,'Seed URL protection failed. The reset was rolled back.',1;
 IF @clearKeywords=0 AND (SELECT COUNT_BIG(*) FROM dbo.SeedKeywords)<>@seedKeywordCount THROW 51014,'Seed Keyword protection failed. The reset was rolled back.',1;
 IF @clearKeywords=1 AND EXISTS(SELECT 1 FROM dbo.SeedKeywords) THROW 51015,'Seed Keywords were not completely cleared. The reset was rolled back.',1;
 IF (SELECT COUNT_BIG(*) FROM dbo.SchemaVersion)<>@schemaVersionCount THROW 51016,'Schema-version protection failed. The reset was rolled back.',1;
 IF OBJECT_ID(N'dbo.HistoryCrawlerSubscriptions',N'U') IS NOT NULL BEGIN DECLARE @subscriptionsAfter BIGINT; EXEC sys.sp_executesql N'SELECT @value=COUNT_BIG(*) FROM dbo.HistoryCrawlerSubscriptions',N'@value bigint OUTPUT',@subscriptionsAfter OUTPUT; IF @subscriptionsAfter<>@subscriptionCount THROW 51022,'Subscription protection failed. The reset was rolled back.',1; END;
 IF OBJECT_ID(N'dbo.HistoryCrawlerSuggestions',N'U') IS NOT NULL BEGIN DECLARE @suggestionsAfter BIGINT; EXEC sys.sp_executesql N'SELECT @value=COUNT_BIG(*) FROM dbo.HistoryCrawlerSuggestions',N'@value bigint OUTPUT',@suggestionsAfter OUTPUT; IF @suggestionsAfter<>@suggestionCount THROW 51023,'Suggestion protection failed. The reset was rolled back.',1; END;
 IF COL_LENGTH(N'dbo.SeedUrls',N'LastCrawledAt') IS NOT NULL EXEC(N'IF EXISTS(SELECT 1 FROM dbo.SeedUrls WHERE LastCrawledAt IS NOT NULL) THROW 51017,''Seed progress was not fully reset.'',1');
 IF COL_LENGTH(N'dbo.SeedUrls',N'ConsecutiveFailures') IS NOT NULL EXEC(N'IF EXISTS(SELECT 1 FROM dbo.SeedUrls WHERE ISNULL(ConsecutiveFailures,0)<>0) THROW 51018,''Seed failure counts were not fully reset.'',1');
 IF COL_LENGTH(N'dbo.SeedUrls',N'AutoDisabled') IS NOT NULL EXEC(N'IF EXISTS(SELECT 1 FROM dbo.SeedUrls WHERE AutoDisabled=1) THROW 51019,''Automatically disabled seeds were not fully reset.'',1');
 COMMIT;
 SELECT @tablesCleared;
END TRY
BEGIN CATCH
 IF CURSOR_STATUS('local','constraint_cursor')>=-1 BEGIN IF CURSOR_STATUS('local','constraint_cursor')>=0 CLOSE constraint_cursor; DEALLOCATE constraint_cursor; END;
 IF CURSOR_STATUS('local','trigger_cursor')>=-1 BEGIN IF CURSOR_STATUS('local','trigger_cursor')>=0 CLOSE trigger_cursor; DEALLOCATE trigger_cursor; END;
 IF CURSOR_STATUS('local','table_cursor')>=-1 BEGIN IF CURSOR_STATUS('local','table_cursor')>=0 CLOSE table_cursor; DEALLOCATE table_cursor; END;
 IF CURSOR_STATUS('local','restore_constraint_cursor')>=-1 BEGIN IF CURSOR_STATUS('local','restore_constraint_cursor')>=0 CLOSE restore_constraint_cursor; DEALLOCATE restore_constraint_cursor; END;
 IF CURSOR_STATUS('local','restore_trigger_cursor')>=-1 BEGIN IF CURSOR_STATUS('local','restore_trigger_cursor')>=0 CLOSE restore_trigger_cursor; DEALLOCATE restore_trigger_cursor; END;
 IF CURSOR_STATUS('local','verify_cursor')>=-1 BEGIN IF CURSOR_STATUS('local','verify_cursor')>=0 CLOSE verify_cursor; DEALLOCATE verify_cursor; END;
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<(long SeedUrls, long SeedKeywords)> VerifyAfterResetAsync(
        SqlConnection connection, bool clearKeywords, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.Parameters.Add("@clearKeywords", SqlDbType.Bit).Value = clearKeywords;
        command.CommandText = @"
IF EXISTS(SELECT 1 FROM dbo.SeedUrls WHERE Url IS NULL OR LTRIM(RTRIM(Url))=N'')
 THROW 51020,'A blank Seed URL remains after verification.',1;
IF @clearKeywords=1 AND EXISTS(SELECT 1 FROM dbo.SeedKeywords)
 THROW 51021,'Seed Keywords were expected to be empty.',1;
SELECT (SELECT COUNT_BIG(*) FROM dbo.SeedUrls),(SELECT COUNT_BIG(*) FROM dbo.SeedKeywords);";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The new-project verification did not return seed counts.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
