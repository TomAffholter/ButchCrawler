using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace ButchCrawler.App
{
    // Enqueue request now supports SeedId (preferred) and SeedUrl as fallback
    public record EnqueueUrlRequest(string Url, int? SeedId = null, string? SeedUrl = null, string? ParentUrl = null, int Depth = 0);

    // Returned when claiming rows from the crawl queue. Includes Id and LockToken for safe claiming.
    public record ClaimedQueueRow(long? Id, string Url, int? Depth = null, int? SeedId = null, string? SeedUrl = null, string? ParentUrl = null, Guid? LockToken = null);

    public partial class SqlStorage
    {
        private readonly string _connectionString;
        private readonly ILogger<SqlStorage> _logger;

        // Primary ctor used by explicit registration factory: new SqlStorage(conn, logger)
        public SqlStorage(string connectionString, ILogger<SqlStorage> logger)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // Backward-compatible: complete by URL (unsafe for multi-worker deployments). Prefer CompleteUrlAsync(id, token).
        public async Task CompleteUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE dbo.CrawlQueue
SET status = N'completed', locked_until = NULL, LockToken = NULL, updated_at = SYSUTCDATETIME()
WHERE Url = @url;
";
                cmd.Parameters.AddWithValue("@url", url);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error completing url {Url}", url);
            }
        }

        // DI-friendly ctor used when AddSingleton<SqlStorage>() is used (IConfiguration provided by DI)
        public SqlStorage(IConfiguration config, ILogger<SqlStorage> logger)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _connectionString = config.GetConnectionString("DefaultConnection")
                                ?? config.GetValue<string>("ConnectionStrings:DefaultConnection")
                                ?? throw new InvalidOperationException("Missing DB connection string");
        }

        // Backward-compatible: fail by URL (unsafe for multi-worker deployments). Prefer FailUrlAsync(id, token).
        public async Task FailUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE dbo.CrawlQueue
SET status = N'failed', locked_until = NULL, LockToken = NULL, updated_at = SYSUTCDATETIME()
WHERE Url = @url;
";
                cmd.Parameters.AddWithValue("@url", url);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error marking url failed {Url}", url);
            }
        }

        // Embedding helpers implemented in SqlStorage.ContentDiffs.cs

        // Return pending count for a seed.
        public async Task<int> GetPendingCountForSeedAsync(string seedUrl)
        {
            if (string.IsNullOrWhiteSpace(seedUrl)) return 0;

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT COUNT(1)
FROM dbo.CrawlQueue
WHERE (status IS NULL OR status = N'pending')
  AND (locked_until IS NULL OR locked_until < SYSUTCDATETIME())
  AND (SeedUrl = @seed OR @seed IS NULL);
";
            cmd.Parameters.AddWithValue("@seed", seedUrl);

            var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return res == null || res == DBNull.Value ? 0 : Convert.ToInt32(res);
        }

        // A failed save must propagate so callers cannot report completion.
        public async Task StoreCrawlResultAsync(string url, int statusCode, string? text)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
MERGE dbo.CrawlResults AS target
USING (SELECT @url AS Url) AS src
    ON target.Url = src.Url
WHEN MATCHED THEN
    UPDATE SET status_code = @status, content = @text, updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Url, status_code, content, created_at)
    VALUES (@url, @status, @text, SYSUTCDATETIME());
";
                cmd.Parameters.AddWithValue("@url", url);
                cmd.Parameters.AddWithValue("@status", statusCode);
                cmd.Parameters.AddWithValue("@text", (object?)text ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error storing crawl result for {Url}", url);
                throw;
            }
        }

        // Upsert visited URL record (simple, resilient)
        public async Task UpsertVisitedUrlAsync(string url, string? contentHashHex, int statusCode)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
MERGE dbo.VisitedUrls AS target
USING (SELECT @url AS Url) AS src
    ON target.Url = src.Url
WHEN MATCHED THEN
    UPDATE SET content_hash = @hash, status_code = @status, last_crawled = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (url, content_hash, status_code, last_crawled)
    VALUES (@url, @hash, @status, SYSUTCDATETIME());
";
                cmd.Parameters.AddWithValue("@url", url);
                cmd.Parameters.AddWithValue("@hash", (object?)contentHashHex ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@status", statusCode);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error upserting visited url {Url}", url);
            }
        }

        // Mark a queued URL completed
        // Mark a queued URL completed - safe variant requires id and matching lock token
        public async Task CompleteUrlAsync(long id, Guid? lockToken)
        {
            if (id <= 0) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE dbo.CrawlQueue
SET status = N'completed', locked_until = NULL, LockToken = NULL, updated_at = SYSUTCDATETIME()
WHERE Id = @id AND (@token IS NULL OR LockToken = @token);
";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@token", (object?)lockToken ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error completing url id {Id}", id);
            }
        }

        // Mark a queued URL failed
        // Mark a queued URL failed (safe variant requires id and matching lock token)
        public async Task FailUrlAsync(long id, Guid? lockToken)
        {
            if (id <= 0) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE dbo.CrawlQueue
SET status = N'failed', locked_until = NULL, LockToken = NULL, updated_at = SYSUTCDATETIME()
WHERE Id = @id AND (@token IS NULL OR LockToken = @token);
";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@token", (object?)lockToken ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error marking url failed id {Id}", id);
            }
        }

        // Insert an error log row
        public async Task LogErrorAsync(string url, string? errorText, DateTime whenUtc)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO dbo.ErrorLogs (Url, Message, LoggedAt)
VALUES (@url, @msg, @at);
";
                cmd.Parameters.AddWithValue("@url", (object?)url ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@msg", (object?)errorText ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@at", whenUtc);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error inserting error log for {Url}", url);
            }
        }

        // Try to return latest extracted content (pdf/doc) if available - best-effort
        public async Task<string?> GetLatestContentAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                // Try PdfResults first
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT TOP (1) ExtractedText
FROM dbo.PdfResults
WHERE Url = @url
ORDER BY ExtractedAt DESC;
";
                    cmd.Parameters.AddWithValue("@url", url);

                    var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (res != null && res != DBNull.Value) return (string)res;
                }

                // Fallback to documents.text
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT TOP (1) text
FROM dbo.documents
WHERE url = @url
ORDER BY last_crawled DESC;
";
                    cmd.Parameters.AddWithValue("@url", url);

                    var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (res != null && res != DBNull.Value) return (string)res;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading latest content for {Url}", url);
                return null;
            }
        }

        // Ensure the database schema version meets the required minimum.
        // Throws InvalidOperationException if the DB is missing the schema_version marker or version < requiredVersion.
        public async Task EnsureSchemaVersionAsync(int requiredVersion)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
IF OBJECT_ID('dbo.SchemaVersion', 'U') IS NULL
    SELECT NULL AS CurrentVersion;
ELSE
    SELECT MAX(Version) AS CurrentVersion FROM dbo.SchemaVersion;
";
                var res = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                int current = 0;
                if (res != null && res != DBNull.Value)
                {
                    try { current = Convert.ToInt32(res); } catch { current = 0; }
                }

                if (current < requiredVersion)
                {
                    throw new InvalidOperationException($"Database schema version {current} is incompatible. Required >= {requiredVersion}. Apply migrations from Scripts/.");
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while checking schema version");
                throw new InvalidOperationException("Failed to verify database schema version. Ensure migrations have been applied.", ex);
            }
        }

    }
}

