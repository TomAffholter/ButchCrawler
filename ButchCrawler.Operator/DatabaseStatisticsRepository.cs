using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed record DatabaseStatistics(long SeedKeywords, long SeedUrls, long DiscoveredUrls);
public sealed record SeedProgress(long Successful, long Problems, long Total);

public sealed class DatabaseStatisticsRepository(string connectionString)
{
    public async Task<SeedProgress> LoadSeedProgressAsync(CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5 };
        using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.CommandTimeout = 15;
        command.CommandText = @"SELECT
 COALESCE(SUM(CONVERT(bigint,CASE WHEN terminal.Outcome=1 THEN 1 ELSE 0 END)),0),
 COALESCE(SUM(CONVERT(bigint,CASE WHEN terminal.Outcome=2 THEN 1 ELSE 0 END)),0),
 COUNT_BIG(*)
FROM dbo.SeedUrls s
LEFT JOIN
(
 SELECT SeedId,CASE
  WHEN MAX(CASE WHEN Status=N'completed' THEN 1 ELSE 0 END)=1 THEN 1
  WHEN MAX(CASE WHEN Status=N'dead-letter' THEN 1 ELSE 0 END)=1 THEN 2
  ELSE 0 END Outcome
 FROM dbo.CrawlQueue
 WHERE SeedId IS NOT NULL AND Depth=0 AND Status IN(N'completed',N'dead-letter')
 GROUP BY SeedId
) terminal ON terminal.SeedId=s.Id
WHERE s.IsActive=1;";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new SeedProgress(0, 0, 0);
        return new SeedProgress(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public async Task<DatabaseStatistics> LoadAsync(CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5 };
        using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.CommandTimeout = 15;
        command.CommandText = @"SELECT
 (SELECT COUNT_BIG(*) FROM dbo.SeedKeywords),
 (SELECT COUNT_BIG(*) FROM dbo.SeedUrls),
 (SELECT COUNT_BIG(*) FROM (
    SELECT DISTINCT q.Url COLLATE Latin1_General_100_BIN2 AS Url
    FROM dbo.CrawlQueue q
    WHERE (NULLIF(LTRIM(RTRIM(q.ParentUrl)),N'') IS NOT NULL OR q.Depth>0)
      AND NOT EXISTS(SELECT 1 FROM dbo.SeedUrls s WHERE s.Url COLLATE Latin1_General_100_BIN2=q.Url COLLATE Latin1_General_100_BIN2)
 ) discovered);";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new InvalidOperationException("Database statistics were not returned.");
        return new DatabaseStatistics(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
