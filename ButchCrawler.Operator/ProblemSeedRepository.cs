using System.Data;
using ButchCrawler.App;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public enum ProblemSeedCleanupAction { Disable, Delete }
public enum ProblemSeedCleanupRule { FailureCount, FirstFailureDate }
public sealed record ProblemSeedCleanupCriteria(
    ProblemSeedCleanupAction Action,
    ProblemSeedCleanupRule Rule,
    int MinimumFailures,
    DateTime FailureBeforeUtc);

public sealed class ProblemSeedRepository(string connectionString)
{
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await SeedHealthSchema.EnsureAsync(connectionString, ct);
            _schemaReady = true;
        }
        finally { _schemaGate.Release(); }
    }

    public async Task<DataTable> LoadAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.CommandText = @"
CREATE TABLE #ProblemSeedIds(Id INT NOT NULL PRIMARY KEY);
INSERT #ProblemSeedIds(Id)
SELECT s.Id FROM dbo.SeedUrls s
WHERE ISNULL(s.HealthStatus,N'healthy') NOT IN(N'healthy',N'retrying')
 OR s.AutoDisabled=1 OR s.DisabledReason IS NOT NULL;

INSERT #ProblemSeedIds(Id)
SELECT DISTINCT h.SeedId FROM dbo.ChallengeHostPauses h
WHERE h.Status=N'paused' AND h.SeedId IS NOT NULL
 AND NOT EXISTS(SELECT 1 FROM #ProblemSeedIds p WHERE p.Id=h.SeedId);

INSERT #ProblemSeedIds(Id)
SELECT DISTINCT s.Id
FROM dbo.CaptchaTasks t JOIN dbo.SeedUrls s ON s.Url=t.Url
WHERE (t.Status IS NULL OR t.Status=N'pending')
 AND NOT EXISTS(SELECT 1 FROM #ProblemSeedIds p WHERE p.Id=s.Id);

INSERT #ProblemSeedIds(Id)
SELECT DISTINCT s.Id
FROM dbo.CaptchaTasks t JOIN dbo.SeedUrls s ON s.Url=t.SeedUrl
WHERE (t.Status IS NULL OR t.Status=N'pending') AND t.SeedUrl IS NOT NULL
 AND NOT EXISTS(SELECT 1 FROM #ProblemSeedIds p WHERE p.Id=s.Id);

SELECT s.Id,COALESCE(h.RepresentativeUrl,c.Url,s.Url) AS Url,s.Url AS [Seed URL],s.IsActive AS Active,
 CASE WHEN h.Host IS NOT NULL THEN N'domain-verification' WHEN c.Id IS NOT NULL THEN N'human-verification' ELSE s.HealthStatus END AS Status,
 s.ConsecutiveFailures AS Failures,s.FirstFailureAt AS [First failed attempt],s.LastFailureStatus AS [HTTP],s.LastFailureAt AS [Last failure],
 s.NextHealthCheckAt AS [Next check],
 CASE WHEN h.Host IS NOT NULL THEN CONCAT(N'Host ',h.Host,N' is paused; ',h.DeferredCount,N' additional URL(s) are waiting for one verification.')
      WHEN c.Id IS NOT NULL THEN N'The site requires a person to complete its verification screen.'
      ELSE COALESCE(s.DisabledReason,s.LastFailureReason) END AS Reason
FROM #ProblemSeedIds p JOIN dbo.SeedUrls s ON s.Id=p.Id
LEFT JOIN dbo.ChallengeHostPauses h ON h.SeedId=s.Id AND h.Status=N'paused'
OUTER APPLY (SELECT TOP (1) t.Id,t.Url FROM dbo.CaptchaTasks t
 WHERE (t.Status IS NULL OR t.Status=N'pending') AND (t.SeedUrl=s.Url OR t.Url=s.Url)
 ORDER BY t.CreatedAt DESC) c
ORDER BY CASE WHEN s.IsActive=0 THEN 0 ELSE 1 END,s.LastFailureAt DESC,s.Url
OPTION(RECOMPILE);";
        using var reader = await command.ExecuteReaderAsync(ct);
        var table = new DataTable();
        for (var column = 0; column < reader.FieldCount; column++)
        {
            var type = reader.GetFieldType(column);
            table.Columns.Add(reader.GetName(column), Nullable.GetUnderlyingType(type) ?? type);
        }
        while (await reader.ReadAsync(ct))
        {
            var row = table.NewRow();
            for (var column = 0; column < reader.FieldCount; column++)
                row[column] = reader.IsDBNull(column) ? DBNull.Value : reader.GetValue(column);
            table.Rows.Add(row);
        }
        return table;
    }

    public async Task RetryNowAsync(int id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"
DECLARE @url NVARCHAR(2000)=(SELECT Url FROM dbo.SeedUrls WHERE Id=@id);
IF @url IS NULL THROW 50015,'Seed URL was not found.',1;
UPDATE dbo.SeedUrls SET IsActive=1,HealthStatus=N'retrying',NextHealthCheckAt=NULL,
 AutoDisabled=0,DisabledReason=NULL WHERE Id=@id;
UPDATE dbo.CrawlQueue SET Status=N'pending',Attempts=0,LockToken=NULL,locked_until=NULL,
 NextAttemptAt=NULL,LeaseExpires=NULL,updated_at=SYSUTCDATETIME()
WHERE SeedId=@id AND Depth=0;
IF @@ROWCOUNT=0
 INSERT dbo.CrawlQueue(Url,SeedId,SeedUrl,ParentUrl,Depth,Status,created_at)
 VALUES(@url,@id,@url,NULL,0,N'pending',SYSUTCDATETIME());";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(ct); transaction.Commit();
        }
        catch { try { transaction.Rollback(); } catch { } throw; }
    }

    public async Task CompleteHumanVerificationAsync(int id, string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var verifiedUri))
            throw new ArgumentException("The selected verification URL is invalid.", nameof(url));
        var verifiedHost = verifiedUri.IdnHost;

        await EnsureSchemaAsync(ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        try
        {
            var relatedUrls = new HashSet<string>(StringComparer.Ordinal) { url };
            using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = @"
SELECT Url FROM dbo.CaptchaTasks WHERE Status IS NULL OR Status=N'pending'
UNION
SELECT Url FROM dbo.CrawlQueue
WHERE (Status=N'dead-letter' AND last_error LIKE N'%Challenge detected:%') OR Status=N'waiting-verification';";
                using var reader = await find.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var candidate = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri) &&
                        string.Equals(candidateUri.IdnHost, verifiedHost, StringComparison.OrdinalIgnoreCase))
                        relatedUrls.Add(candidate!);
                }
            }

            using (var create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = "CREATE TABLE #VerifiedUrls(Url NVARCHAR(2000) NOT NULL);";
                await create.ExecuteNonQueryAsync(ct);
            }
            var table = new DataTable(); table.Columns.Add("Url", typeof(string));
            foreach (var relatedUrl in relatedUrls) table.Rows.Add(relatedUrl);
            using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulk.DestinationTableName = "#VerifiedUrls";
                bulk.ColumnMappings.Add("Url", "Url");
                await bulk.WriteToServerAsync(table, ct);
            }

            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"
UPDATE t SET Status=N'solved',SolvedAt=SYSUTCDATETIME(),Attempts=ISNULL(Attempts,0)+1
FROM dbo.CaptchaTasks t JOIN #VerifiedUrls v
 ON v.Url COLLATE Latin1_General_100_BIN2=t.Url COLLATE Latin1_General_100_BIN2
WHERE t.Status IS NULL OR t.Status=N'pending';

IF OBJECT_ID(N'dbo.ChallengeFailureHistory',N'U') IS NOT NULL
 UPDATE h SET ResolutionStatus=N'solved',ResolvedAt=SYSUTCDATETIME()
 FROM dbo.ChallengeFailureHistory h JOIN #VerifiedUrls v
  ON v.Url COLLATE Latin1_General_100_BIN2=h.Url COLLATE Latin1_General_100_BIN2
 WHERE h.ResolutionStatus=N'pending';

UPDATE q SET Status=N'pending',Attempts=0,LockToken=NULL,locked_until=NULL,
 NextAttemptAt=NULL,LeaseExpires=NULL,last_error=NULL,updated_at=SYSUTCDATETIME()
FROM dbo.CrawlQueue q JOIN #VerifiedUrls v
 ON v.Url COLLATE Latin1_General_100_BIN2=q.Url COLLATE Latin1_General_100_BIN2
WHERE q.Status IN(N'dead-letter',N'waiting-verification');

UPDATE dbo.ChallengeHostPauses SET Status=N'resolved',ResolvedAt=SYSUTCDATETIME()
WHERE Host=@host AND Status=N'paused';

UPDATE s SET IsActive=1,HealthStatus=N'retrying',NextHealthCheckAt=NULL,
 AutoDisabled=0,DisabledReason=NULL
FROM dbo.SeedUrls s
WHERE s.Id=@id OR EXISTS(
 SELECT 1 FROM dbo.CrawlQueue q JOIN #VerifiedUrls v
  ON v.Url COLLATE Latin1_General_100_BIN2=q.Url COLLATE Latin1_General_100_BIN2
 WHERE q.SeedId=s.Id);

DECLARE @seedUrl NVARCHAR(2000)=(SELECT Url FROM dbo.SeedUrls WHERE Id=@id);
IF @seedUrl IS NULL THROW 50015,'Seed URL was not found.',1;
UPDATE dbo.CrawlQueue SET Status=N'pending',Attempts=0,LockToken=NULL,locked_until=NULL,
 NextAttemptAt=NULL,LeaseExpires=NULL,last_error=NULL,updated_at=SYSUTCDATETIME()
WHERE SeedId=@id AND Depth=0;
IF @@ROWCOUNT=0
 INSERT dbo.CrawlQueue(Url,SeedId,SeedUrl,ParentUrl,Depth,Status,created_at)
 VALUES(@seedUrl,@id,@seedUrl,NULL,0,N'pending',SYSUTCDATETIME());";
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@host", verifiedHost.ToLowerInvariant());
            await command.ExecuteNonQueryAsync(ct);
            transaction.Commit();
        }
        catch { try { transaction.Rollback(); } catch { } throw; }
    }

    public async Task DisableAsync(int id, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"
UPDATE dbo.SeedUrls SET IsActive=0,HealthStatus=N'disabled',AutoDisabled=0,
 DisabledReason=N'Disabled manually from Problem seeds',NextHealthCheckAt=NULL WHERE Id=@id;
UPDATE dbo.CrawlQueue SET Status=N'dead-letter',LockToken=NULL,locked_until=NULL,
 updated_at=SYSUTCDATETIME() WHERE SeedId=@id AND Status=N'pending';";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(ct); transaction.Commit();
        }
        catch { try { transaction.Rollback(); } catch { } throw; }
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"
DELETE FROM dbo.CrawlQueue WHERE SeedId=@id;
DELETE FROM dbo.SeedUrls WHERE Id=@id;
IF @@ROWCOUNT<>1 THROW 50016,'Seed URL was not deleted.',1;";
            command.Parameters.AddWithValue("@id", id);
            await command.ExecuteNonQueryAsync(ct); transaction.Commit();
        }
        catch { try { transaction.Rollback(); } catch { } throw; }
    }

    public async Task<DataTable> PreviewCleanupAsync(ProblemSeedCleanupCriteria criteria, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.CommandText = @"
SELECT s.Id,s.Url,s.HealthStatus AS Status,s.IsActive AS Active,
 s.ConsecutiveFailures AS Failures,s.FirstFailureAt AS [First failed attempt],
 s.LastFailureAt AS [Last failure],s.LastFailureStatus AS [HTTP],
 COALESCE(s.DisabledReason,s.LastFailureReason) AS Reason
FROM dbo.SeedUrls s
WHERE ISNULL(s.ConsecutiveFailures,0)>0
 AND ISNULL(s.HealthStatus,N'healthy') NOT IN(N'healthy',N'retrying')
 AND (@delete=1 OR s.IsActive=1)
 AND ((@rule=0 AND s.ConsecutiveFailures>=@failures)
   OR (@rule=1 AND s.FirstFailureAt IS NOT NULL AND s.FirstFailureAt<@before))
ORDER BY s.ConsecutiveFailures DESC,s.FirstFailureAt,s.Url;";
        AddCleanupParameters(command, criteria);
        using var reader = await command.ExecuteReaderAsync(ct);
        var table = new DataTable();
        for (var column = 0; column < reader.FieldCount; column++)
        {
            var type = reader.GetFieldType(column);
            table.Columns.Add(reader.GetName(column), Nullable.GetUnderlyingType(type) ?? type);
        }
        while (await reader.ReadAsync(ct))
        {
            var row = table.NewRow();
            for (var column = 0; column < reader.FieldCount; column++)
                row[column] = reader.IsDBNull(column) ? DBNull.Value : reader.GetValue(column);
            table.Rows.Add(row);
        }
        return table;
    }

    public async Task<int> ApplyCleanupAsync(ProblemSeedCleanupCriteria criteria, IReadOnlyCollection<int> previewedIds,
        CancellationToken ct = default)
    {
        if (previewedIds.Count == 0) return 0;
        await EnsureSchemaAsync(ct);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var create = connection.CreateCommand())
            {
                create.Transaction = transaction;
                create.CommandText = "CREATE TABLE #PreviewedIds(Id INT NOT NULL PRIMARY KEY);";
                await create.ExecuteNonQueryAsync(ct);
            }
            var ids = new DataTable(); ids.Columns.Add("Id", typeof(int));
            foreach (var id in previewedIds.Distinct()) ids.Rows.Add(id);
            using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulk.DestinationTableName = "#PreviewedIds";
                bulk.ColumnMappings.Add("Id", "Id");
                await bulk.WriteToServerAsync(ids, ct);
            }

            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandTimeout = 180;
            command.CommandText = @"
SELECT s.Id INTO #Eligible
FROM dbo.SeedUrls s JOIN #PreviewedIds p ON p.Id=s.Id
WHERE ISNULL(s.ConsecutiveFailures,0)>0
 AND ISNULL(s.HealthStatus,N'healthy') NOT IN(N'healthy',N'retrying')
 AND (@delete=1 OR s.IsActive=1)
 AND ((@rule=0 AND s.ConsecutiveFailures>=@failures)
   OR (@rule=1 AND s.FirstFailureAt IS NOT NULL AND s.FirstFailureAt<@before));

IF @delete=0
BEGIN
 UPDATE s SET IsActive=0,HealthStatus=N'disabled',AutoDisabled=0,
  DisabledReason=N'Disabled by reviewed problem-seed cleanup',NextHealthCheckAt=NULL
 FROM dbo.SeedUrls s JOIN #Eligible e ON e.Id=s.Id;
 DECLARE @disabled INT=@@ROWCOUNT;
 UPDATE q SET Status=N'dead-letter',LockToken=NULL,locked_until=NULL,updated_at=SYSUTCDATETIME()
 FROM dbo.CrawlQueue q JOIN #Eligible e ON e.Id=q.SeedId WHERE q.Status=N'pending';
 SELECT @disabled;
END
ELSE
BEGIN
 IF OBJECT_ID(N'dbo.ChallengeHostPauses',N'U') IS NOT NULL
  UPDATE h SET SeedId=NULL FROM dbo.ChallengeHostPauses h JOIN #Eligible e ON e.Id=h.SeedId;
 DELETE q FROM dbo.CrawlQueue q JOIN #Eligible e ON e.Id=q.SeedId;
 DELETE s FROM dbo.SeedUrls s JOIN #Eligible e ON e.Id=s.Id;
 SELECT @@ROWCOUNT;
END;";
            AddCleanupParameters(command, criteria);
            var affected = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            transaction.Commit();
            return affected;
        }
        catch { try { transaction.Rollback(); } catch { } throw; }
    }

    private static void AddCleanupParameters(SqlCommand command, ProblemSeedCleanupCriteria criteria)
    {
        command.Parameters.AddWithValue("@delete", criteria.Action == ProblemSeedCleanupAction.Delete);
        command.Parameters.AddWithValue("@rule", criteria.Rule == ProblemSeedCleanupRule.FailureCount ? 0 : 1);
        command.Parameters.AddWithValue("@failures", Math.Max(1, criteria.MinimumFailures));
        command.Parameters.AddWithValue("@before", criteria.FailureBeforeUtc);
    }
}
