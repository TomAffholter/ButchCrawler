using System.Data;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed record ResetTable(string Property, long Rows, bool Preserved);

public sealed class DatabaseResetService
{
    private readonly string _connectionString;
    private readonly string _expectedDatabase;
    public DatabaseResetService(string connectionString, string expectedDatabase = "ButchCrawlerDB")
    { _connectionString = connectionString; _expectedDatabase = expectedDatabase; }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
            if (!string.Equals(connection.Database, _expectedDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Reset is restricted to {_expectedDatabase}.");
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.spWipeData',N'P') IS NOT NULL AND OBJECT_ID(N'dbo.SeedUrls',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SeedKeywords',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SchemaVersion',N'U') IS NOT NULL THEN 1 ELSE 0 END";
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) != 1)
                throw new InvalidOperationException("Required seed tables or dbo.spWipeData were not found.");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<IReadOnlyList<ResetTable>> PreviewAsync(bool clearKeywords)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT s.name,t.name,COALESCE(SUM(p.rows),0)
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
LEFT JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)
GROUP BY s.name,t.name ORDER BY s.name,t.name";
        using var reader = await command.ExecuteReaderAsync();
        var rows = new List<ResetTable>();
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0); var table = reader.GetString(1);
            var preserved = schema == "dbo" && (table is "SeedUrls" or "SchemaVersion" || (!clearKeywords && table == "SeedKeywords"));
            rows.Add(new ResetTable(schema + "." + table, reader.GetInt64(2), preserved));
        }
        return rows;
    }

    public async Task ResetAsync(bool clearKeywords)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand(); command.CommandTimeout = 600;
        command.Parameters.Add("@clearKeywords", SqlDbType.Bit).Value = clearKeywords;
        command.CommandText = @"
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 DECLARE @lockResult INT;
 EXEC @lockResult = sys.sp_getapplock @Resource=N'ButchCrawler.DatabaseReset', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=0;
 IF @lockResult < 0 THROW 51010, 'Another database reset is running.', 1;
 IF COL_LENGTH('dbo.CrawlQueue','locked_until') IS NOT NULL
 BEGIN
  IF EXISTS (SELECT 1 FROM dbo.CrawlQueue WHERE locked_until > SYSUTCDATETIME() AND LockToken IS NOT NULL)
   THROW 51011, 'Active crawl leases exist. Stop all crawlers and wait for leases to expire.', 1;
 END;
 -- The existing procedure disables ALL constraints but restores only foreign keys.
 -- Capture both types so this wrapper restores check constraints and original trust state too.
 DECLARE @constraints TABLE (Qualified NVARCHAR(517), ConstraintName SYSNAME, Untrusted BIT);
 INSERT @constraints SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+'.'+QUOTENAME(OBJECT_NAME(parent_object_id)),name,is_not_trusted FROM sys.check_constraints WHERE is_disabled=0
 UNION ALL SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+'.'+QUOTENAME(OBJECT_NAME(parent_object_id)),name,is_not_trusted FROM sys.foreign_keys WHERE is_disabled=0;
 DECLARE @urls BIGINT=(SELECT COUNT_BIG(*) FROM dbo.SeedUrls), @keywords BIGINT=(SELECT COUNT_BIG(*) FROM dbo.SeedKeywords);
 EXEC dbo.spWipeData;
 IF @clearKeywords=1 DELETE FROM dbo.SeedKeywords;
 DECLARE @qualified NVARCHAR(517), @name SYSNAME, @untrusted BIT, @sql NVARCHAR(MAX);
 DECLARE constraints_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified,ConstraintName,Untrusted FROM @constraints;
 OPEN constraints_cursor; FETCH NEXT FROM constraints_cursor INTO @qualified,@name,@untrusted;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'ALTER TABLE '+@qualified+N' '+CASE WHEN @untrusted=0 THEN N'WITH CHECK CHECK' ELSE N'NOCHECK' END+N' CONSTRAINT '+QUOTENAME(@name)+N';';
  IF @untrusted=1 SET @sql+=N'ALTER TABLE '+@qualified+N' CHECK CONSTRAINT '+QUOTENAME(@name)+N';';
  EXEC(@sql);
  FETCH NEXT FROM constraints_cursor INTO @qualified,@name,@untrusted;
 END;
 CLOSE constraints_cursor; DEALLOCATE constraints_cursor;
 DECLARE @reseed NVARCHAR(100);
 DECLARE identity_cursor CURSOR LOCAL FAST_FORWARD FOR
 SELECT QUOTENAME(s.name)+'.'+QUOTENAME(t.name),CONVERT(NVARCHAR(100),CONVERT(DECIMAL(38,0),i.seed_value)-CONVERT(DECIMAL(38,0),i.increment_value))
 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.identity_columns i ON i.object_id=t.object_id
 WHERE i.last_value IS NOT NULL AND NOT(s.name='dbo' AND (t.name IN ('SeedUrls','SchemaVersion') OR (t.name='SeedKeywords' AND @clearKeywords=0)));
 OPEN identity_cursor; FETCH NEXT FROM identity_cursor INTO @qualified,@reseed;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'IF EXISTS(SELECT 1 FROM '+@qualified+N') THROW 51012, ''A table expected to be empty still has data.'', 1; DBCC CHECKIDENT ('''+REPLACE(@qualified,'''','''''')+N''',RESEED,'+@reseed+N') WITH NO_INFOMSGS;';
  EXEC(@sql); FETCH NEXT FROM identity_cursor INTO @qualified,@reseed;
 END;
 CLOSE identity_cursor; DEALLOCATE identity_cursor;
 IF (SELECT COUNT_BIG(*) FROM dbo.SeedUrls)<>@urls THROW 51013, 'Seed URL count changed unexpectedly.', 1;
 IF @clearKeywords=0 AND (SELECT COUNT_BIG(*) FROM dbo.SeedKeywords)<>@keywords THROW 51014, 'Keyword count changed unexpectedly.', 1;
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;";
        await command.ExecuteNonQueryAsync();
    }
}
