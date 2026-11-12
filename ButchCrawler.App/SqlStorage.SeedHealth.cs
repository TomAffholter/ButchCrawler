using Microsoft.Data.SqlClient;

namespace ButchCrawler.App;

public partial class SqlStorage
{
    public Task EnsureSeedHealthSchemaAsync(CancellationToken ct = default) => SeedHealthSchema.EnsureAsync(_connectionString, ct);

    public async Task<int> RequeueDueSeedHealthChecksAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE q SET q.Status=N'pending', q.Attempts=0, q.LockToken=NULL, q.locked_until=NULL,
 q.NextAttemptAt=NULL, q.LeaseExpires=NULL, q.updated_at=SYSUTCDATETIME()
FROM dbo.CrawlQueue q
JOIN dbo.SeedUrls s ON s.Id=q.SeedId
WHERE s.IsActive=1 AND s.NextHealthCheckAt<=SYSUTCDATETIME()
 AND q.Depth=0 AND q.Status=N'dead-letter';
DECLARE @count INT=@@ROWCOUNT;
UPDATE dbo.SeedUrls SET HealthStatus=N'retrying',NextHealthCheckAt=NULL
WHERE IsActive=1 AND NextHealthCheckAt<=SYSUTCDATETIME();
SELECT @count;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task RecordSeedFailureAsync(int seedId, int statusCode, string reason, string healthStatus,
        bool deactivate, DateTime? nextCheckUtc, CancellationToken ct = default)
    {
        if (seedId <= 0) return;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE dbo.SeedUrls SET
 FirstFailureAt=CASE WHEN ISNULL(ConsecutiveFailures,0)=0 OR FirstFailureAt IS NULL THEN SYSUTCDATETIME() ELSE FirstFailureAt END,
 ConsecutiveFailures=ISNULL(ConsecutiveFailures,0)+1,
 LastFailureAt=SYSUTCDATETIME(), LastFailureStatus=@status,
 LastFailureReason=LEFT(@reason,1000), HealthStatus=@health,
 NextHealthCheckAt=@nextCheck,
 IsActive=CASE WHEN @deactivate=1 THEN 0 ELSE IsActive END,
 AutoDisabled=CASE WHEN @deactivate=1 THEN 1 ELSE AutoDisabled END,
 DisabledReason=CASE WHEN @deactivate=1 THEN LEFT(@reason,500) ELSE DisabledReason END
WHERE Id=@id;";
        command.Parameters.AddWithValue("@id", seedId);
        command.Parameters.AddWithValue("@status", statusCode);
        command.Parameters.AddWithValue("@reason", reason ?? string.Empty);
        command.Parameters.AddWithValue("@health", healthStatus);
        command.Parameters.AddWithValue("@nextCheck", (object?)nextCheckUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@deactivate", deactivate);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordSeedSuccessAsync(int seedId, CancellationToken ct = default)
    {
        if (seedId <= 0) return;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE dbo.SeedUrls SET HealthStatus=N'healthy',ConsecutiveFailures=0,FirstFailureAt=NULL,
 LastFailureAt=NULL,LastFailureStatus=NULL,LastFailureReason=NULL,NextHealthCheckAt=NULL,
 DisabledReason=CASE WHEN AutoDisabled=1 THEN NULL ELSE DisabledReason END,
 AutoDisabled=0,LastCrawledAt=SYSUTCDATETIME()
WHERE Id=@id;";
        command.Parameters.AddWithValue("@id", seedId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
