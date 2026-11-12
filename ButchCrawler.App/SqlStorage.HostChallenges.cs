using Microsoft.Data.SqlClient;

namespace ButchCrawler.App;

public partial class SqlStorage
{
    public async Task<IReadOnlyList<string>> GetPausedChallengeHostsAsync(CancellationToken ct = default)
    {
        var hosts = new List<string>();
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Host FROM dbo.ChallengeHostPauses WHERE Status=N'paused';";
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            if (!reader.IsDBNull(0)) hosts.Add(reader.GetString(0));
        return hosts;
    }

    public async Task PauseChallengeHostAsync(string host, string representativeUrl, int? seedId,
        string? challengeType, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
MERGE dbo.ChallengeHostPauses WITH (HOLDLOCK) AS target
USING(SELECT @host Host) AS source ON source.Host=target.Host
WHEN MATCHED THEN UPDATE SET RepresentativeUrl=@url,SeedId=COALESCE(@seedId,target.SeedId),
 ChallengeType=@type,ChallengeCount=target.ChallengeCount+1,Status=N'paused',
 LastChallengeAt=SYSUTCDATETIME(),ResolvedAt=NULL
WHEN NOT MATCHED THEN INSERT(Host,RepresentativeUrl,SeedId,ChallengeType,ChallengeCount,Status)
 VALUES(@host,@url,@seedId,@type,2,N'paused');";
        command.Parameters.AddWithValue("@host", host);
        command.Parameters.AddWithValue("@url", representativeUrl);
        command.Parameters.AddWithValue("@seedId", (object?)seedId ?? DBNull.Value);
        command.Parameters.AddWithValue("@type", (object?)challengeType ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeferForChallengeHostAsync(long queueId, Guid? lockToken, string host,
        CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"
UPDATE dbo.CrawlQueue SET Status=N'waiting-verification',LockToken=NULL,locked_until=NULL,
 last_error=N'Waiting for domain verification',updated_at=SYSUTCDATETIME()
WHERE Id=@id AND (@token IS NULL OR LockToken=@token);
DECLARE @affected INT=@@ROWCOUNT;
IF @affected=1 UPDATE dbo.ChallengeHostPauses SET DeferredCount=DeferredCount+1,
 LastChallengeAt=SYSUTCDATETIME() WHERE Host=@host AND Status=N'paused';
SELECT @affected;";
            command.Parameters.AddWithValue("@id", queueId);
            command.Parameters.AddWithValue("@token", (object?)lockToken ?? DBNull.Value);
            command.Parameters.AddWithValue("@host", host);
            var changed = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
            transaction.Commit();
            return changed;
        }
        catch { try { transaction.Rollback(); } catch { } throw; }
    }

    public async Task<int> RetireRemovedAuthenticatedSiteQueueAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE dbo.CrawlQueue SET Status=N'dead-letter',LockToken=NULL,locked_until=NULL,
 last_error=N'Automatic authenticated-site crawling was removed. Use a normal browser and import permitted saved files.',updated_at=SYSUTCDATETIME()
WHERE Status=N'waiting-authentication';
SELECT @@ROWCOUNT;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }
}
