using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App
{
    public partial class SqlStorage
    {
        // Shared by HTML, PDF and other document handlers. Optional migration 010 is required.
        private async Task SaveKeywordHitsInTransactionAsync(SqlConnection conn, SqlTransaction tran, string url, string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || url.Length > 2000) return;
            using var exists = conn.CreateCommand();
            exists.Transaction = tran;
            exists.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.CrawlKeywordHits', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.CrawlKeywordHits', N'UrlHash') IS NOT NULL THEN 1 ELSE 0 END";
            if (Convert.ToInt32(await exists.ExecuteScalarAsync().ConfigureAwait(false)) != 1) return;
            var active = new List<(int Id, string Keyword)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tran;
                cmd.CommandText = "SELECT Id, Keyword FROM dbo.SeedKeywords WHERE IsActive = 1";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                    active.Add((reader.GetInt32(0), reader.GetString(1)));
            }
            foreach (var (id, term) in active)
            {
                if (string.IsNullOrWhiteSpace(term)) continue;
                int count = 0, first = -1, at = 0;
                while ((at = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    if (first < 0) first = at;
                    count++;
                    at += term.Length;
                }
                using var hit = conn.CreateCommand();
                hit.Transaction = tran;
                hit.CommandText = @"
DELETE FROM dbo.CrawlKeywordHits WHERE UrlHash = HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), @url)) AND KeywordId = @id AND @count = 0;
IF @count > 0
BEGIN
    UPDATE dbo.CrawlKeywordHits WITH (UPDLOCK, SERIALIZABLE)
        SET MatchCount = @count, FirstSnippet = @snippet, LastMatchedAtUtc = SYSUTCDATETIME(), Url = @url
        WHERE UrlHash = HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), @url)) AND KeywordId = @id;
    IF @@ROWCOUNT = 0
        INSERT dbo.CrawlKeywordHits (Url, UrlHash, KeywordId, MatchCount, FirstSnippet)
        VALUES (@url, HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), @url)), @id, @count, @snippet);
END";
                hit.Parameters.AddWithValue("@url", url);
                hit.Parameters.AddWithValue("@id", id);
                hit.Parameters.AddWithValue("@count", count);
                var start = Math.Max(0, first - 150);
                hit.Parameters.AddWithValue("@snippet", count == 0 ? DBNull.Value : (object)text.Substring(start, Math.Min(1000, text.Length - start)));
                await hit.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        public async Task RecordKeywordHitsAsync(string url, string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(url)) return;
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var tran = conn.BeginTransaction();
            try
            {
                await SaveKeywordHitsInTransactionAsync(conn, tran, url, text).ConfigureAwait(false);
                tran.Commit();
            }
            catch
            {
                try { tran.Rollback(); } catch { }
                throw;
            }
        }

        public async Task<List<SeedUrl>> GetActiveSeedUrlsAsync()
        {
            var ret = new List<SeedUrl>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE IsActive = 1
ORDER BY Priority DESC,
 CASE WHEN LastCrawledAt IS NULL THEN 0 ELSE 1 END,
 LastCrawledAt ASC, CreatedAt ASC;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    // Note: columns adjusted to include Id at index 0
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading active seed urls");
            }

            return ret;
        }

        // Upsert a set of seed keywords (simple best-effort implementation).
        public async Task UpsertKeywordsAsync(IEnumerable<string>? keywords, string? source = null)
        {
            if (keywords == null) return;

            var list = keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var tran = conn.BeginTransaction();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tran;
                    cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.SeedKeywords WHERE Keyword = @kw)
    INSERT INTO dbo.SeedKeywords (Keyword, Source, CreatedAt) VALUES (@kw, @src, SYSUTCDATETIME());
";
                    var pKw = cmd.CreateParameter(); pKw.ParameterName = "@kw"; cmd.Parameters.Add(pKw);
                    var pSrc = cmd.CreateParameter(); pSrc.ParameterName = "@src"; pSrc.Value = (object?)source ?? DBNull.Value; cmd.Parameters.Add(pSrc);

                    foreach (var kw in list)
                    {
                        pKw.Value = kw;
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tran.Commit();
                }
                catch
                {
                    try { tran.Rollback(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error upserting seed keywords");
            }
        }

        public async Task<List<string>> GetKeywordsAsync(bool throwOnError = false)
        {
            var ret = new List<string>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Keyword FROM dbo.SeedKeywords WHERE IsActive = 1 ORDER BY Keyword;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) ret.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed keywords");
                if (throwOnError) throw;
            }
            return ret;
        }

        // Return a dictionary of visited urls -> contentHash (best-effort, returns empty on error)
        public async Task<Dictionary<string, string?>> GetVisitedUrlsAsync()
        {
            var ret = new Dictionary<string, string?>(StringComparer.Ordinal);
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT url, content_hash FROM dbo.VisitedUrls;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var url = !reader.IsDBNull(0) ? reader.GetString(0) : string.Empty;
                    var hash = !reader.IsDBNull(1) ? reader.GetString(1) : null;
                    if (!string.IsNullOrWhiteSpace(url)) ret[url] = hash;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading visited urls");
            }
            return ret;
        }

        // Release leases that have expired so other workers can claim them.
        public async Task ReleaseExpiredLeasesAsync()
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE dbo.CrawlQueue
SET locked_until = NULL, LockToken = NULL
WHERE locked_until IS NOT NULL AND locked_until < SYSUTCDATETIME();
";
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error releasing expired leases");
            }
        }

        // Renew a lease for a claimed row. Returns true only when id+token match an active row.
        public async Task<bool> RenewLeaseAsync(long id, Guid? lockToken, TimeSpan lease)
        {
            if (id <= 0 || !lockToken.HasValue) return false;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE dbo.CrawlQueue
SET locked_until = DATEADD(SECOND, @leaseSecs, SYSUTCDATETIME())
WHERE Id = @id
  AND LockToken = @token
  AND (status IS NULL OR status = N'pending');
";
                cmd.Parameters.AddWithValue("@leaseSecs", Convert.ToInt32(lease.TotalSeconds));
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@token", lockToken.Value);

                var affected = await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return affected == 1;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error renewing lease for queue row {Id}", id);
                return false;
            }
        }

        // Persist fetch failure and transition queue row to retry/dead-letter based on attempts.
        public async Task<string> HandleFetchFailureAsync(
            long queueId,
            Guid? lockToken,
            string url,
            int statusCode,
            string failureReason,
            int maxAttempts,
            TimeSpan retryDelay,
            string? source,
            string? userAgent,
            string? remoteIp,
            string? asn)
        {
            if (queueId <= 0) throw new ArgumentOutOfRangeException(nameof(queueId));
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var tran = conn.BeginTransaction();
            try
            {
                var hasAttempts = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "Attempts", tran).ConfigureAwait(false)
                                  || await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "attempts", tran).ConfigureAwait(false);
                var hasLastErrorPascal = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "LastError", tran).ConfigureAwait(false);
                var hasLastErrorSnake = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "last_error", tran).ConfigureAwait(false);
                var hasLeaseExpires = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "LeaseExpires", tran).ConfigureAwait(false);
                var hasNextAttemptPascal = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "NextAttemptAt", tran).ConfigureAwait(false);
                var hasNextAttemptSnake = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "next_attempt_at", tran).ConfigureAwait(false);

                int currentAttempts = 0;
                if (hasAttempts)
                {
                    using var sel = conn.CreateCommand();
                    sel.Transaction = tran;
                    sel.CommandText = @"
SELECT ISNULL(Attempts, 0)
FROM dbo.CrawlQueue WITH (UPDLOCK, ROWLOCK)
WHERE Id = @id AND (@token IS NULL OR LockToken = @token);
";
                    sel.Parameters.AddWithValue("@id", queueId);
                    sel.Parameters.AddWithValue("@token", (object?)lockToken ?? DBNull.Value);
                    var obj = await sel.ExecuteScalarAsync().ConfigureAwait(false);
                    if (obj == null || obj == DBNull.Value)
                        throw new InvalidOperationException($"Queue row not found or lease mismatch for id={queueId}");
                    currentAttempts = Convert.ToInt32(obj);
                }
                else
                {
                    using var sel = conn.CreateCommand();
                    sel.Transaction = tran;
                    sel.CommandText = @"
SELECT 1
FROM dbo.CrawlQueue WITH (UPDLOCK, ROWLOCK)
WHERE Id = @id AND (@token IS NULL OR LockToken = @token);
";
                    sel.Parameters.AddWithValue("@id", queueId);
                    sel.Parameters.AddWithValue("@token", (object?)lockToken ?? DBNull.Value);
                    var obj = await sel.ExecuteScalarAsync().ConfigureAwait(false);
                    if (obj == null || obj == DBNull.Value)
                        throw new InvalidOperationException($"Queue row not found or lease mismatch for id={queueId}");
                }

                var nextAttempts = currentAttempts + 1;
                var deadLetter = nextAttempts >= Math.Max(1, maxAttempts);

                using (var resultCmd = conn.CreateCommand())
                {
                    resultCmd.Transaction = tran;
                    resultCmd.CommandText = @"
MERGE dbo.CrawlResults AS target
USING (SELECT @url AS Url) AS src
    ON target.Url = src.Url
WHEN MATCHED THEN
    UPDATE SET status_code = @status, content = @text, updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Url, status_code, content, created_at)
    VALUES (@url, @status, @text, SYSUTCDATETIME());
";
                    resultCmd.Parameters.AddWithValue("@url", url);
                    resultCmd.Parameters.AddWithValue("@status", statusCode);
                    resultCmd.Parameters.AddWithValue("@text", (object?)failureReason ?? DBNull.Value);
                    await resultCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                using var update = conn.CreateCommand();
                update.Transaction = tran;

                var statusValue = deadLetter ? "dead-letter" : "pending";
                var nextAttemptAt = DateTime.UtcNow.Add(retryDelay);

                var setParts = new List<string>
                {
                    "status = @status",
                    deadLetter ? "locked_until = NULL" : "locked_until = @nextAttempt",
                    "LockToken = NULL",
                    "updated_at = SYSUTCDATETIME()"
                };
                if (hasAttempts) setParts.Add("Attempts = @attempts");
                if (hasLastErrorSnake) setParts.Add("last_error = @lastError");
                else if (hasLastErrorPascal) setParts.Add("LastError = @lastError");
                if (hasLeaseExpires) setParts.Add("LeaseExpires = @leaseExpires");
                if (hasNextAttemptPascal) setParts.Add("NextAttemptAt = @nextAttempt");
                else if (hasNextAttemptSnake) setParts.Add("next_attempt_at = @nextAttempt");

                update.CommandText = $@"
UPDATE dbo.CrawlQueue
SET {string.Join(", ", setParts)}
WHERE Id = @id AND (@token IS NULL OR LockToken = @token);
";
                update.Parameters.AddWithValue("@status", statusValue);
                update.Parameters.AddWithValue("@id", queueId);
                update.Parameters.AddWithValue("@token", (object?)lockToken ?? DBNull.Value);
                update.Parameters.AddWithValue("@attempts", nextAttempts);
                update.Parameters.AddWithValue("@lastError", (object?)failureReason ?? DBNull.Value);
                update.Parameters.AddWithValue("@leaseExpires", nextAttemptAt);
                update.Parameters.AddWithValue("@nextAttempt", nextAttemptAt);

                var affected = await update.ExecuteNonQueryAsync().ConfigureAwait(false);
                if (affected != 1)
                    throw new InvalidOperationException($"Failed to transition failed queue row id={queueId}; lease token mismatch or row missing.");

                tran.Commit();
                return statusValue;
            }
            catch
            {
                try { tran.Rollback(); } catch { }
                throw;
            }
        }

        // Claim up to maxCount urls by setting a lease and returning the claimed rows (with optional metadata).
        public async Task<List<ClaimedQueueRow>> ClaimUrlsAsync(int maxCount, System.TimeSpan lease)
        {
            var ret = new List<ClaimedQueueRow>();
            if (maxCount <= 0) return ret;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                // Detect which optional columns exist so we can output/read them safely
                var hasSeedId = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "SeedId").ConfigureAwait(false);
                var hasSeedUrl = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "SeedUrl").ConfigureAwait(false);
                var hasDepth = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "Depth").ConfigureAwait(false);
                var hasParent = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "ParentUrl").ConfigureAwait(false);
                var hasId = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "Id").ConfigureAwait(false);
                var hasLockToken = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "LockToken").ConfigureAwait(false);

                if (!hasId || !hasLockToken)
                {
                    _logger.LogWarning("CrawlQueue schema missing Id or LockToken; refusing unsafe multi-worker claim path.");
                    return ret;
                }

                using var cmd = conn.CreateCommand();
                var outputCols = new List<string>();
                if (hasId) outputCols.Add("inserted.Id");
                outputCols.Add("inserted.Url");
                if (hasDepth) outputCols.Add("inserted.Depth");
                if (hasSeedId) outputCols.Add("inserted.SeedId");
                if (hasSeedUrl) outputCols.Add("inserted.SeedUrl");
                if (hasParent) outputCols.Add("inserted.ParentUrl");
                if (hasLockToken) outputCols.Add("inserted.LockToken");

                var outputClause = string.Join(", ", outputCols);

                cmd.CommandText = $@"
WITH cte AS (
    SELECT TOP (@max) * FROM dbo.CrawlQueue WITH (ROWLOCK, READPAST, UPDLOCK)
    WHERE (status IS NULL OR status = N'pending') AND (locked_until IS NULL OR locked_until < SYSUTCDATETIME())
    ORDER BY created_at ASC
)
UPDATE cte
SET locked_until = DATEADD(SECOND, @leaseSecs, SYSUTCDATETIME())
    { (hasLockToken ? ", LockToken = NEWID()" : string.Empty) }
OUTPUT {outputClause};
";

                cmd.Parameters.AddWithValue("@max", maxCount);
                cmd.Parameters.AddWithValue("@leaseSecs", Convert.ToInt32(lease.TotalSeconds));

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var idx = 0;
                    long? id = null;
                    if (hasId)
                    {
                        if (!reader.IsDBNull(idx)) id = reader.GetInt64(idx);
                        idx++;
                    }

                    string url = !reader.IsDBNull(idx) ? reader.GetString(idx) : string.Empty;
                    idx++;

                    int? depth = null;
                    int? seedId = null;
                    string? seedUrl = null;
                    string? parentUrl = null;
                    Guid? lockToken = null;

                    if (hasDepth)
                    {
                        if (!reader.IsDBNull(idx)) depth = reader.GetInt32(idx);
                        idx++;
                    }
                    if (hasSeedId)
                    {
                        if (!reader.IsDBNull(idx)) seedId = reader.GetInt32(idx);
                        idx++;
                    }
                    if (hasSeedUrl)
                    {
                        if (!reader.IsDBNull(idx)) seedUrl = reader.GetString(idx);
                        idx++;
                    }
                    if (hasParent)
                    {
                        if (!reader.IsDBNull(idx)) parentUrl = reader.GetString(idx);
                        idx++;
                    }
                    if (hasLockToken)
                    {
                        if (!reader.IsDBNull(idx)) lockToken = reader.GetGuid(idx);
                        idx++;
                    }

                    ret.Add(new ClaimedQueueRow(id, url, depth, seedId, seedUrl, parentUrl, lockToken));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error claiming urls");
            }

            return ret;
        }



        public async Task<List<SeedUrl>> GetSeedUrlsByKeywordAsync(string keyword)
        {
            var ret = new List<SeedUrl>();
            if (string.IsNullOrWhiteSpace(keyword)) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE IsActive = 1 AND (
      (Tags IS NOT NULL AND CHARINDEX(@kw, Tags) > 0)
   OR (Category IS NOT NULL AND Category = @kw)
   OR (Url LIKE '%' + @kw + '%'))
ORDER BY Priority DESC, CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@kw", keyword);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls for keyword {Keyword}", keyword);
            }

            return ret;
        }

        // Helper to check whether a column exists in a table (schema dbo)
        private static async Task<bool> ColumnExistsAsync(SqlConnection conn, string schema, string table, string column, SqlTransaction? transaction = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
SELECT 1
FROM sys.columns c
JOIN sys.tables t ON c.object_id = t.object_id
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @schema AND t.name = @table AND c.name = @column;
";
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            cmd.Parameters.AddWithValue("@column", column);
            var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return res != null;
        }

        // New overload: enqueue URLs with metadata (SeedId preferred).
        public async Task EnqueueUrlsAsync(IEnumerable<EnqueueUrlRequest>? requests)
        {
            if (requests == null) return;

            var reqList = requests.Where(r => !string.IsNullOrWhiteSpace(r.Url))
                                  .GroupBy(r => $"{r.Url}\u001f{r.SeedId?.ToString() ?? ""}\u001f{r.SeedUrl ?? ""}", StringComparer.Ordinal)
                                  .Select(g => g.First())
                                  .ToList();

            if (reqList.Count == 0) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var tran = conn.BeginTransaction();
                try
                {
                    // detect whether schema supports SeedId or SeedUrl
                    var hasSeedId = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "SeedId", tran).ConfigureAwait(false);
                    var hasSeedUrl = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "SeedUrl", tran).ConfigureAwait(false);

                    using (var create = conn.CreateCommand())
                    {
                        create.Transaction = tran;
                        create.CommandText = @"CREATE TABLE #QueueRequests(
Url NVARCHAR(2000) NOT NULL,SeedId INT NULL,SeedUrl NVARCHAR(2000) NULL,
ParentUrl NVARCHAR(2000) NULL,Depth INT NOT NULL);";
                        await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    var table = new DataTable();
                    table.Columns.Add("Url", typeof(string)); table.Columns.Add("SeedId", typeof(int));
                    table.Columns.Add("SeedUrl", typeof(string)); table.Columns.Add("ParentUrl", typeof(string));
                    table.Columns.Add("Depth", typeof(int));
                    foreach (var r in reqList) table.Rows.Add(r.Url, (object?)r.SeedId ?? DBNull.Value,
                        (object?)r.SeedUrl ?? DBNull.Value, (object?)r.ParentUrl ?? DBNull.Value, r.Depth);
                    using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.CheckConstraints, tran)
                    { DestinationTableName = "#QueueRequests", BatchSize = 5000, BulkCopyTimeout = 120 })
                        await bulk.WriteToServerAsync(table).ConfigureAwait(false);

                    using var cmd = conn.CreateCommand(); cmd.Transaction = tran; cmd.CommandTimeout = 120;
                    cmd.CommandText = hasSeedId ? @"
INSERT dbo.CrawlQueue(Url,SeedId,SeedUrl,ParentUrl,Depth,Status,created_at)
SELECT r.Url,r.SeedId,r.SeedUrl,r.ParentUrl,r.Depth,N'pending',SYSUTCDATETIME()
FROM #QueueRequests r
WHERE NOT EXISTS(SELECT 1 FROM dbo.CrawlQueue q WITH(UPDLOCK,HOLDLOCK)
 WHERE q.Url=r.Url AND (q.SeedId=r.SeedId OR (q.SeedId IS NULL AND r.SeedId IS NULL)));"
                    : hasSeedUrl ? @"
INSERT dbo.CrawlQueue(Url,SeedUrl,ParentUrl,Depth,Status,created_at)
SELECT r.Url,r.SeedUrl,r.ParentUrl,r.Depth,N'pending',SYSUTCDATETIME()
FROM #QueueRequests r
WHERE NOT EXISTS(SELECT 1 FROM dbo.CrawlQueue q WITH(UPDLOCK,HOLDLOCK)
 WHERE q.Url=r.Url AND (q.SeedUrl=r.SeedUrl OR (q.SeedUrl IS NULL AND r.SeedUrl IS NULL)));"
                    : @"
INSERT dbo.CrawlQueue(Url,Status,created_at)
SELECT r.Url,N'pending',SYSUTCDATETIME() FROM #QueueRequests r
WHERE NOT EXISTS(SELECT 1 FROM dbo.CrawlQueue q WITH(UPDLOCK,HOLDLOCK) WHERE q.Url=r.Url);";
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                    tran.Commit();
                }
                catch
                {
                    try { tran.Rollback(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error enqueuing urls with metadata");
                throw;
            }
        }

        // Atomically persist HTML crawl result + visited state + discovered children + queue completion.
        public async Task<int> CompleteHtmlCrawlAsync(
            long queueId,
            Guid? lockToken,
            string url,
            int statusCode,
            string? text,
            DateTime whenUtc,
            string? contentHashHex,
            string? userAgent,
            string? remoteIp,
            string? asn,
            string? source,
            IEnumerable<EnqueueUrlRequest>? discovered)
        {
            if (queueId <= 0) throw new ArgumentOutOfRangeException(nameof(queueId));
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));

            var discoveredList = (discovered ?? Enumerable.Empty<EnqueueUrlRequest>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Url))
                .GroupBy(r => r.Url, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            var insertedCount = 0;
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var tran = conn.BeginTransaction();
            try
            {
                // 1) Store crawl result (rich schema)
                using (var resultCmd = conn.CreateCommand())
                {
                    resultCmd.Transaction = tran;
                    resultCmd.CommandText = @"
MERGE dbo.CrawlResults AS target
USING (SELECT @url AS Url) AS src
    ON target.Url = src.Url
WHEN MATCHED THEN
    UPDATE SET status_code = @status, content = @text, updated_at = @at
WHEN NOT MATCHED THEN
    INSERT (Url, status_code, content, created_at)
    VALUES (@url, @status, @text, @at);
";
                    resultCmd.Parameters.AddWithValue("@url", url);
                    resultCmd.Parameters.AddWithValue("@status", statusCode);
                    resultCmd.Parameters.AddWithValue("@text", (object?)text ?? DBNull.Value);
                    resultCmd.Parameters.AddWithValue("@at", whenUtc);
                    resultCmd.Parameters.AddWithValue("@ua", (object?)userAgent ?? DBNull.Value);
                    resultCmd.Parameters.AddWithValue("@rip", (object?)remoteIp ?? DBNull.Value);
                    resultCmd.Parameters.AddWithValue("@asn", (object?)asn ?? DBNull.Value);
                    resultCmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);
                    await resultCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await SaveKeywordHitsInTransactionAsync(conn, tran, url, text).ConfigureAwait(false);

                // 2) Upsert visited state (stable schema columns)
                using (var visitedCmd = conn.CreateCommand())
                {
                    visitedCmd.Transaction = tran;
                    visitedCmd.CommandText = @"
MERGE dbo.VisitedUrls AS target
USING (SELECT @url AS Url) AS src
    ON target.Url = src.Url
WHEN MATCHED THEN
    UPDATE SET content_hash = @hash, status_code = @status, last_crawled = @at, updated_at = @at
WHEN NOT MATCHED THEN
    INSERT (url, content_hash, status_code, last_crawled)
    VALUES (@url, @hash, @status, @at);
";
                    visitedCmd.Parameters.AddWithValue("@url", url);
                    visitedCmd.Parameters.AddWithValue("@hash", (object?)contentHashHex ?? DBNull.Value);
                    visitedCmd.Parameters.AddWithValue("@status", statusCode);
                    visitedCmd.Parameters.AddWithValue("@at", whenUtc);
                    await visitedCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                // 3) Enqueue discovered children using same transaction
                if (discoveredList.Count > 0)
                {
                    var hasSeedId = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "SeedId", tran).ConfigureAwait(false);
                    var hasSeedUrl = await ColumnExistsAsync(conn, "dbo", "CrawlQueue", "SeedUrl", tran).ConfigureAwait(false);

                    using var enqueueCmd = conn.CreateCommand();
                    enqueueCmd.Transaction = tran;

                    if (hasSeedId)
                    {
                        enqueueCmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.CrawlQueue WITH (UPDLOCK, HOLDLOCK) WHERE Url = @url AND (SeedId = @seedId OR (@seedId IS NULL AND SeedId IS NULL)))
BEGIN
    INSERT INTO dbo.CrawlQueue (Url, SeedId, SeedUrl, ParentUrl, Depth, Status, created_at)
    VALUES (@url, @seedId, @seedUrl, @parent, @depth, N'pending', SYSUTCDATETIME());
END
";
                    }
                    else if (hasSeedUrl)
                    {
                        enqueueCmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.CrawlQueue WITH (UPDLOCK, HOLDLOCK) WHERE Url = @url AND (SeedUrl = @seedUrl OR (@seedUrl IS NULL AND SeedUrl IS NULL)))
BEGIN
    INSERT INTO dbo.CrawlQueue (Url, SeedUrl, ParentUrl, Depth, Status, created_at)
    VALUES (@url, @seedUrl, @parent, @depth, N'pending', SYSUTCDATETIME());
END
";
                    }
                    else
                    {
                        enqueueCmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.CrawlQueue WITH (UPDLOCK, HOLDLOCK) WHERE Url = @url)
BEGIN
    INSERT INTO dbo.CrawlQueue (Url, Status, created_at)
    VALUES (@url, N'pending', SYSUTCDATETIME());
END
";
                    }

                    var pUrl = enqueueCmd.CreateParameter(); pUrl.ParameterName = "@url"; enqueueCmd.Parameters.Add(pUrl);
                    var pSeedId = enqueueCmd.CreateParameter(); pSeedId.ParameterName = "@seedId"; enqueueCmd.Parameters.Add(pSeedId);
                    var pSeedUrl = enqueueCmd.CreateParameter(); pSeedUrl.ParameterName = "@seedUrl"; enqueueCmd.Parameters.Add(pSeedUrl);
                    var pParent = enqueueCmd.CreateParameter(); pParent.ParameterName = "@parent"; enqueueCmd.Parameters.Add(pParent);
                    var pDepth = enqueueCmd.CreateParameter(); pDepth.ParameterName = "@depth"; enqueueCmd.Parameters.Add(pDepth);

                    foreach (var r in discoveredList)
                    {
                        pUrl.Value = r.Url;
                        pSeedId.Value = (object?)r.SeedId ?? DBNull.Value;
                        pSeedUrl.Value = (object?)r.SeedUrl ?? DBNull.Value;
                        pParent.Value = (object?)r.ParentUrl ?? DBNull.Value;
                        pDepth.Value = r.Depth;
                        var added = await enqueueCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        if (added > 0) insertedCount += added;
                    }
                }

                // 4) Complete only the claimed queue row that matches this lease token
                using (var completeCmd = conn.CreateCommand())
                {
                    completeCmd.Transaction = tran;
                    completeCmd.CommandText = @"
UPDATE dbo.CrawlQueue
SET status = N'completed', locked_until = NULL, LockToken = NULL, updated_at = SYSUTCDATETIME()
WHERE Id = @id AND (@token IS NULL OR LockToken = @token);
";
                    completeCmd.Parameters.AddWithValue("@id", queueId);
                    completeCmd.Parameters.AddWithValue("@token", (object?)lockToken ?? DBNull.Value);
                    var affected = await completeCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    if (affected != 1)
                    {
                        throw new InvalidOperationException($"Failed to complete claimed queue row id={queueId}; lease token mismatch or row missing.");
                    }
                }

                tran.Commit();
                return insertedCount;
            }
            catch
            {
                try { tran.Rollback(); } catch { }
                throw;
            }
        }

        // existing EnqueueUrlsAsync(IEnumerable<string>) remains unchanged below...
        public async Task EnqueueUrlsAsync(IEnumerable<string> urls)
        {
            if (urls == null) return;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var tran = conn.BeginTransaction();
                try
                {
                    foreach (var u in urls)
                    {
                        if (string.IsNullOrWhiteSpace(u)) continue;
                        using var cmd = conn.CreateCommand();
                        cmd.Transaction = tran;
                        cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.CrawlQueue WHERE Url = @url)
    INSERT INTO dbo.CrawlQueue (Url, Status, created_at) VALUES (@url, N'pending', SYSUTCDATETIME());
";
                        cmd.Parameters.AddWithValue("@url", u);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tran.Commit();
                }
                catch
                {
                    try { tran.Rollback(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error enqueuing urls");
                throw;
            }
        }

        public async Task<List<SeedUrl>> GetSeedUrlsByParentAsync(string parentUrl)
        {
            var ret = new List<SeedUrl>();
            if (string.IsNullOrWhiteSpace(parentUrl)) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE IsActive = 1 AND (ParentUrl = @parentUrl OR Url = @parentUrl)
ORDER BY Priority DESC, CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@parentUrl", parentUrl);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls by parent url {ParentUrl}", parentUrl);
            }

            return ret;
        }

        public async Task<List<string>> GetPendingUrlsAsync(int maxCount)
        {
            var ret = new List<string>();
            if (maxCount <= 0) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT TOP (@count) Url FROM dbo.CrawlQueue WHERE Status = N'pending' ORDER BY created_at ASC;";
                cmd.Parameters.AddWithValue("@count", maxCount);
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) ret.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading pending urls");
            }
            return ret;
        }

        public async Task<List<string>> GetActiveSeedUrlsDistinctDomainAsync()
        {
            var ret = new List<string>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT DISTINCT Domain
FROM dbo.SeedUrls
WHERE IsActive = 1 AND Domain IS NOT NULL
ORDER BY Domain;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) ret.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading active seed urls distinct domains");
            }
            return ret;
        }

        public async Task<List<string>> GetSeedUrlsDistinctCategoryAsync()
        {
            var ret = new List<string>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT DISTINCT Category
FROM dbo.SeedUrls
WHERE Category IS NOT NULL
ORDER BY Category;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) ret.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls distinct categories");
            }
            return ret;
        }

        public async Task<List<string>> GetSeedUrlsDistinctTagAsync()
        {
            var ret = new List<string>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT DISTINCT Tags
FROM dbo.SeedUrls
WHERE Tags IS NOT NULL
ORDER BY Tags;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) ret.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls distinct tags");
            }
            return ret;
        }

        public async Task<List<string>> GetSeedUrlSourcesAsync()
        {
            var ret = new List<string>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT DISTINCT Source
FROM dbo.SeedUrls
WHERE Source IS NOT NULL
ORDER BY Source;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) ret.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls sources");
            }
            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsWithPendingChildrenAsync()
        {
            var ret = new List<SeedUrl>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT DISTINCT s.Id, s.Url, s.Domain, s.Category, s.Priority, s.MaxDepth, s.FollowExternal, s.IsActive, s.Notes, s.CreatedAt, s.LastCrawledAt, s.SourceType, s.DiscoverySource, s.Tags
FROM dbo.SeedUrls s
JOIN dbo.CrawlQueue q ON s.Url = q.Url
WHERE q.Status = N'pending' AND s.IsActive = 1
ORDER BY s.Priority DESC, s.CreatedAt ASC;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls with pending children");
            }

            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsReadyForCrawlAsync(int maxUrls)
        {
            var ret = new List<SeedUrl>();
            if (maxUrls <= 0) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT TOP (@maxUrls) s.Id, s.Url, s.Domain, s.Category, s.Priority, s.MaxDepth, s.FollowExternal, s.IsActive, s.Notes, s.CreatedAt, s.LastCrawledAt, s.SourceType, s.DiscoverySource, s.Tags
FROM dbo.SeedUrls s
JOIN dbo.CrawlQueue q ON s.Url = q.Url
WHERE q.Status = N'ready' AND s.IsActive = 1
ORDER BY s.Priority DESC, s.CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@maxUrls", maxUrls);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls ready for crawl");
            }

            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsToUpdateAsync(int maxUrls)
        {
            var ret = new List<SeedUrl>();
            if (maxUrls <= 0) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT TOP (@maxUrls) s.Id, s.Url, s.Domain, s.Category, s.Priority, s.MaxDepth, s.FollowExternal, s.IsActive, s.Notes, s.CreatedAt, s.LastCrawledAt, s.SourceType, s.DiscoverySource, s.Tags
FROM dbo.SeedUrls s
JOIN dbo.CrawlQueue q ON s.Url = q.Url
WHERE q.Status = N'update' AND s.IsActive = 1
ORDER BY s.Priority DESC, s.CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@maxUrls", maxUrls);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls to update");
            }

            return ret;
        }

        public async Task<List<string>> GetSeedUrlParentUrlsAsync()
        {
            var ret = new List<string>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT DISTINCT ParentUrl
FROM dbo.SeedUrls
WHERE ParentUrl IS NOT NULL
ORDER BY ParentUrl;
";
                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) ret.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed url parent urls");
            }
            return ret;
        }

        public async Task<bool> TestAndSetUrlActiveAsync(string url, bool isActive)
        {
            var ret = false;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var tran = conn.BeginTransaction();
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tran;
                        cmd.CommandText = @"
UPDATE dbo.SeedUrls
SET IsActive = @isActive
WHERE Url = @url;
";
                        cmd.Parameters.AddWithValue("@url", url);
                        cmd.Parameters.AddWithValue("@isActive", isActive);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // check row count to determine if update occurred
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tran;
                        cmd.CommandText = "SELECT @@ROWCOUNT;";
                        var rowCount = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                        ret = (rowCount is int count && count > 0);
                    }

                    tran.Commit();
                }
                catch
                {
                    try { tran.Rollback(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error setting seed url active status");
            }
            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsByStatusAsync(string status, int maxCount)
        {
            var ret = new List<SeedUrl>();
            if (string.IsNullOrWhiteSpace(status) || maxCount <= 0) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT TOP (@maxCount) s.Id, s.Url, s.Domain, s.Category, s.Priority, s.MaxDepth, s.FollowExternal, s.IsActive, s.Notes, s.CreatedAt, s.LastCrawledAt, s.SourceType, s.DiscoverySource, s.Tags
FROM dbo.SeedUrls s
JOIN dbo.CrawlQueue q ON s.Url = q.Url
WHERE q.Status = @status
ORDER BY s.Priority DESC, s.CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@maxCount", maxCount);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls by status {Status}", status);
            }

            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsByDomainAsync(string domain)
        {
            var ret = new List<SeedUrl>();
            if (string.IsNullOrWhiteSpace(domain)) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE Domain = @domain
ORDER BY Priority DESC, CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@domain", domain);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domainVal = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domainVal, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls by domain {Domain}", domain);
            }

            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsByCategoryAsync(string category)
        {
            var ret = new List<SeedUrl>();
            if (string.IsNullOrWhiteSpace(category)) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE Category = @category
ORDER BY Priority DESC, CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@category", category);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var categoryVal = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, categoryVal, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls by category {Category}", category);
            }

            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsByTagAsync(string tag)
        {
            var ret = new List<SeedUrl>();
            if (string.IsNullOrWhiteSpace(tag)) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE Tags IS NOT NULL AND CHARINDEX(@tag, Tags) > 0
ORDER BY Priority DESC, CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@tag", tag);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls by tag {Tag}", tag);
            }

            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsBySourceAsync(string source)
        {
            var ret = new List<SeedUrl>();
            if (string.IsNullOrWhiteSpace(source)) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE Source = @source
ORDER BY Priority DESC, CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@source", source);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls by source {Source}", source);
            }

            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsByDateRangeAsync(DateTime startDate, DateTime endDate)
        {
            var ret = new List<SeedUrl>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE CreatedAt >= @startDate AND CreatedAt <= @endDate
ORDER BY CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@startDate", startDate);
                cmd.Parameters.AddWithValue("@endDate", endDate);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls by date range {StartDate} to {EndDate}", startDate, endDate);
            }

            return ret;
        }

        public async Task<List<SeedUrl>> GetSeedUrlsByAuthorAsync(string author)
        {
            var ret = new List<SeedUrl>();
            if (string.IsNullOrWhiteSpace(author)) return ret;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags
FROM dbo.SeedUrls
WHERE Author = @author
ORDER BY Priority DESC, CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@author", author);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var id = !reader.IsDBNull(0) ? reader.GetInt32(0) : 0;
                    var url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty;
                    var domain = !reader.IsDBNull(2) ? reader.GetString(2) : null;
                    var category = !reader.IsDBNull(3) ? reader.GetString(3) : null;
                    var priority = !reader.IsDBNull(4) ? reader.GetInt32(4) : 0;
                    var maxDepth = !reader.IsDBNull(5) ? reader.GetInt32(5) : 0;
                    var followExternal = !reader.IsDBNull(6) ? reader.GetBoolean(6) : false;
                    var isActive = !reader.IsDBNull(7) ? reader.GetBoolean(7) : true;
                    var notes = !reader.IsDBNull(8) ? reader.GetString(8) : null;
                    var createdAt = !reader.IsDBNull(9) ? reader.GetDateTime(9) : DateTime.UtcNow;
                    var lastCrawled = !reader.IsDBNull(10) ? (DateTime?)reader.GetDateTime(10) : null;
                    var sourceType = !reader.IsDBNull(11) ? reader.GetString(11) : null;
                    var discoverySource = !reader.IsDBNull(12) ? reader.GetString(12) : null;
                    var tags = !reader.IsDBNull(13) ? reader.GetString(13) : null;

                    ret.Add(new SeedUrl(id, url, domain, category, priority, maxDepth, followExternal, isActive, notes, createdAt, lastCrawled, sourceType, discoverySource, tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading seed urls by author {Author}", author);
            }

            return ret;
        }

        public async Task UpsertSeedUrlAsync(SeedUrl seedUrl)
        {
            if (seedUrl == null) throw new ArgumentNullException(nameof(seedUrl));
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var tran = conn.BeginTransaction();
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tran;
                        cmd.CommandText = @"
MERGE INTO dbo.SeedUrls AS target
USING (SELECT @url AS Url) AS source
ON target.Url = source.Url
WHEN MATCHED THEN
    UPDATE SET
        Domain = @domain,
        Category = @category,
        Priority = @priority,
        MaxDepth = @maxDepth,
        FollowExternal = @followExternal,
        IsActive = @isActive,
        Notes = @notes,
        CreatedAt = @createdAt,
        LastCrawledAt = @lastCrawledAt,
        SourceType = @sourceType,
        DiscoverySource = @discoverySource,
        Tags = @tags
WHEN NOT MATCHED THEN
    INSERT (Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags)
    VALUES (@url, @domain, @category, @priority, @maxDepth, @followExternal, @isActive, @notes, @createdAt, @lastCrawledAt, @sourceType, @discoverySource, @tags);
";
                        cmd.Parameters.AddWithValue("@url", seedUrl.Url);
                        cmd.Parameters.AddWithValue("@domain", (object?)seedUrl.Domain ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@category", (object?)seedUrl.Category ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@priority", seedUrl.Priority);
                        cmd.Parameters.AddWithValue("@maxDepth", seedUrl.MaxDepth);
                        cmd.Parameters.AddWithValue("@followExternal", seedUrl.FollowExternal);
                        cmd.Parameters.AddWithValue("@isActive", seedUrl.IsActive);
                        cmd.Parameters.AddWithValue("@notes", (object?)seedUrl.Notes ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@createdAt", seedUrl.CreatedAt);
                        cmd.Parameters.AddWithValue("@lastCrawledAt", (object?)seedUrl.LastCrawledAt ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@sourceType", (object?)seedUrl.SourceType ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@discoverySource", (object?)seedUrl.DiscoverySource ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@tags", (object?)seedUrl.Tags ?? DBNull.Value);

                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tran.Commit();
                }
                catch
                {
                    try { tran.Rollback(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error upserting seed url");
                throw;
            }
        }

        public async Task DeleteSeedUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var tran = conn.BeginTransaction();
                try
                {
                    // delete from SeedUrls
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tran;
                        cmd.CommandText = @"
DELETE FROM dbo.SeedUrls
WHERE Url = @url;
";
                        cmd.Parameters.AddWithValue("@url", url);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    // delete from CrawlQueue
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tran;
                        cmd.CommandText = @"
DELETE FROM dbo.CrawlQueue
WHERE Url = @url;
";
                        cmd.Parameters.AddWithValue("@url", url);
                        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    tran.Commit();
                }
                catch
                {
                    try { tran.Rollback(); } catch { }
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error deleting seed url");
                throw;
            }
        }

        public async Task<long> GetSeedUrlCountAsync()
        {
            var count = 0L;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COUNT_BIG(*) FROM dbo.SeedUrls;";
                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                count = (res is long l) ? l : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error counting seed urls");
            }

            return count;
        }

        public async Task<long> GetActiveSeedUrlCountAsync()
        {
            var count = 0L;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COUNT_BIG(*) FROM dbo.SeedUrls WHERE IsActive = 1;";
                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                count = (res is long l) ? l : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error counting active seed urls");
            }

            return count;
        }

        public async Task<long> GetInactiveSeedUrlCountAsync()
        {
            var count = 0L;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COUNT_BIG(*) FROM dbo.SeedUrls WHERE IsActive = 0;";
                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                count = (res is long l) ? l : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error counting inactive seed urls");
            }

            return count;
        }

        public async Task<long> GetQueuedUrlCountAsync()
        {
            var count = 0L;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COUNT_BIG(*) FROM dbo.CrawlQueue;";
                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                count = (res is long l) ? l : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error counting queued urls");
            }

            return count;
        }

        public async Task<long> GetPendingUrlCountAsync()
        {
            var count = 0L;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COUNT_BIG(*) FROM dbo.CrawlQueue WHERE Status = N'pending';";
                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                count = (res is long l) ? l : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error counting pending urls");
            }

            return count;
        }

        public async Task<long> GetLockedUrlCountAsync()
        {
            var count = 0L;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT COUNT_BIG(*) FROM dbo.CrawlQueue WHERE Status = N'locked';";
                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                count = (res is long l) ? l : 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error counting locked urls");
            }

            return count;
        }

        public async Task<(int Pending, int Locked)> GetPendingAndLockedCountsAsync()
        {
            try
            {
                var (p, l, c) = await GetCrawlQueueStatsAsync().ConfigureAwait(false);
                return (p, l);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading pending/locked counts");
                return (0, 0);
            }
        }
    }
}
