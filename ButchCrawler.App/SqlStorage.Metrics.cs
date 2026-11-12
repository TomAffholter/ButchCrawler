using System;
using System.Data;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App
{
    public partial class SqlStorage
    {
        /// <summary>
        /// Returns counts for pending, locked (leased) and completed crawl queue rows.
        /// </summary>
        public async Task<(int Pending, int Locked, int Completed)> GetCrawlQueueStatsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    SUM(CASE WHEN (status IS NULL OR status = N'pending') AND (locked_until IS NULL OR locked_until < SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS Pending,
    SUM(CASE WHEN locked_until IS NOT NULL AND locked_until > SYSUTCDATETIME() THEN 1 ELSE 0 END) AS Locked,
    SUM(CASE WHEN status = N'completed' THEN 1 ELSE 0 END) AS Completed
FROM dbo.CrawlQueue;
";
            try
            {
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var pending = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var locked = !reader.IsDBNull(1) ? reader.GetInt32(1) : 0;
                    var completed = !reader.IsDBNull(2) ? reader.GetInt32(2) : 0;
                    return (pending, locked, completed);
                }

                return (0, 0, 0);
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex, "DB error reading CrawlQueue stats");
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// Returns the number of rows in VisitedUrls. Cheap COUNT(*) query.
        /// </summary>
        public async Task<int> GetVisitedCountAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COUNT(1) FROM dbo.VisitedUrls;";

            try
            {
                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return res == null || res == DBNull.Value ? 0 : Convert.ToInt32(res);
            }
            catch (SqlException ex)
            {
                _logger.LogWarning(ex, "DB error reading VisitedUrls count");
                return 0;
            }
        }

        /// <summary>
        /// Returns terminal root seed outcomes and the total active seed URLs.
        /// Child pages are deliberately excluded from this user-facing progress value.
        /// </summary>
        public async Task<(long Successful, long Problems, long Total)> GetActiveSeedProgressAsync(CancellationToken cancellationToken = default)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT
    COALESCE(SUM(CONVERT(bigint, CASE WHEN terminal.Outcome = 1 THEN 1 ELSE 0 END)), 0) AS Successful,
    COALESCE(SUM(CONVERT(bigint, CASE WHEN terminal.Outcome = 2 THEN 1 ELSE 0 END)), 0) AS Problems,
    COUNT_BIG(*) AS Total
FROM dbo.SeedUrls s
LEFT JOIN
(
    SELECT SeedId, CASE
        WHEN MAX(CASE WHEN Status=N'completed' THEN 1 ELSE 0 END)=1 THEN 1
        WHEN MAX(CASE WHEN Status=N'dead-letter' THEN 1 ELSE 0 END)=1 THEN 2
        ELSE 0 END AS Outcome
    FROM dbo.CrawlQueue
    WHERE SeedId IS NOT NULL
      AND Depth = 0
      AND Status IN (N'completed',N'dead-letter')
    GROUP BY SeedId
) terminal ON terminal.SeedId = s.Id
WHERE s.IsActive = 1;";

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return (0, 0, 0);
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
    }
}
