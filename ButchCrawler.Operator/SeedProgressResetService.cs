using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed record SeedRestartPreview(long ActiveSeeds, long ExistingRootRows);

/// <summary>
/// Restarts root seed scheduling without deleting captures, results, matches,
/// documents, discovered URLs, or visited history.
/// </summary>
public sealed class SeedProgressResetService
{
    private readonly string _connectionString;
    private readonly string _expectedDatabase;

    public SeedProgressResetService(string connectionString, string expectedDatabase = "ButchCrawlerDB")
    {
        _connectionString = connectionString;
        _expectedDatabase = expectedDatabase;
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync();
            if (!string.Equals(connection.Database, _expectedDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Seed restart is restricted to {_expectedDatabase}.");

            using var check = connection.CreateCommand();
            check.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.SeedUrls',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.CrawlQueue',N'U') IS NOT NULL THEN 1 ELSE 0 END";
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) != 1)
                throw new InvalidOperationException("The seed URL or crawl queue table was not found.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<SeedRestartPreview> PreviewAsync()
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT
 (SELECT COUNT_BIG(*) FROM dbo.SeedUrls WHERE IsActive=1),
 (SELECT COUNT_BIG(*) FROM dbo.CrawlQueue q JOIN dbo.SeedUrls s ON s.Id=q.SeedId
  WHERE s.IsActive=1 AND q.Depth=0);";
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new SeedRestartPreview(0, 0);
        return new SeedRestartPreview(reader.GetInt64(0), reader.GetInt64(1));
    }

    public async Task<long> RestartAsync()
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = @"
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 DECLARE @lockResult INT;
 EXEC @lockResult=sys.sp_getapplock @Resource=N'ButchCrawler.SeedProgressRestart',
  @LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
 IF @lockResult<0 THROW 51110, 'Another seed restart is running.', 1;
 IF EXISTS(SELECT 1 FROM dbo.CrawlQueue WHERE locked_until>SYSUTCDATETIME() AND LockToken IS NOT NULL)
  THROW 51111, 'Active crawl work exists. Stop all crawlers and wait for their work to finish.', 1;

 UPDATE q SET q.Status=N'pending',q.Attempts=0,q.LockToken=NULL,q.locked_until=NULL,
  q.NextAttemptAt=NULL,q.LeaseExpires=NULL,q.last_error=NULL,q.updated_at=SYSUTCDATETIME()
 FROM dbo.CrawlQueue q
 JOIN dbo.SeedUrls s ON s.Id=q.SeedId
 WHERE s.IsActive=1 AND q.Depth=0;
 DECLARE @restarted BIGINT=@@ROWCOUNT;

 -- This is scheduling metadata only. Captures and collected research are untouched.
 UPDATE dbo.SeedUrls SET LastCrawledAt=NULL WHERE IsActive=1;
 COMMIT;
 SELECT @restarted;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
