using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App
{
    public partial class SqlStorage
    {
        public async Task<long?> CreateCaptchaTaskAsync(string url, string? seedUrl, string? htmlSnippet,
            int? httpStatus = null, string? challengeType = null, string? failureReason = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
IF OBJECT_ID(N'dbo.CaptchaTasks', N'U') IS NULL
    SELECT CAST(NULL AS BIGINT);
ELSE
BEGIN
    DECLARE @taskId BIGINT = (
        SELECT TOP (1) Id FROM dbo.CaptchaTasks
        WHERE Url = @url AND (Status IS NULL OR Status = N'pending') ORDER BY CreatedAt DESC);
    IF @taskId IS NULL
    BEGIN
        INSERT dbo.CaptchaTasks (Url, SeedUrl, HtmlSnippet, Status)
        VALUES (@url, @seedUrl, @snippet, N'pending');
        SET @taskId=CAST(SCOPE_IDENTITY() AS BIGINT);
    END
    IF OBJECT_ID(N'dbo.ChallengeFailureHistory',N'U') IS NOT NULL
     INSERT dbo.ChallengeFailureHistory(CaptchaTaskId,Url,SeedUrl,HttpStatus,ChallengeType,FailureReason)
     VALUES(@taskId,@url,@seedUrl,@httpStatus,@challengeType,@reason);
    SELECT @taskId;
END";
                cmd.Parameters.AddWithValue("@url", url);
                cmd.Parameters.AddWithValue("@seedUrl", (object?)seedUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@snippet", (object?)htmlSnippet ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@httpStatus", (object?)httpStatus ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@challengeType", (object?)challengeType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@reason", (object?)failureReason ?? DBNull.Value);
                var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return result == null || result == DBNull.Value ? null : Convert.ToInt64(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error creating challenge-review task for {Url}", url);
                return null;
            }
        }

        // Retrieve pending captcha tasks (best-effort). Returns empty list on error.
        public async Task<List<CaptchaTask>> GetPendingCaptchaTasksAsync(int limit = 20)
        {
            var ret = new List<CaptchaTask>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT TOP (@limit) Id, Url, SeedUrl, CreatedAt, Status, ScreenshotPath, HtmlSnippet, SolutionJson, SolvedAt, Attempts
FROM dbo.CaptchaTasks
WHERE (Status IS NULL OR Status = N'pending')
ORDER BY CreatedAt ASC;
";
                cmd.Parameters.AddWithValue("@limit", limit);

                using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var t = new CaptchaTask
                    {
                        // Id column may be INT or BIGINT depending on schema; read as object and convert to Int64 safely
                        Id = !reader.IsDBNull(0) ? (long)Convert.ToInt64(reader.GetValue(0)) : 0,
                        Url = !reader.IsDBNull(1) ? reader.GetString(1) : string.Empty,
                        SeedUrl = !reader.IsDBNull(2) ? reader.GetString(2) : null,
                        CreatedAt = !reader.IsDBNull(3) ? reader.GetDateTime(3) : DateTime.MinValue,
                        Status = !reader.IsDBNull(4) ? reader.GetString(4) : "pending",
                        ScreenshotPath = !reader.IsDBNull(5) ? reader.GetString(5) : null,
                        HtmlSnippet = !reader.IsDBNull(6) ? reader.GetString(6) : null,
                        SolutionJson = !reader.IsDBNull(7) ? reader.GetString(7) : null,
                        SolvedAt = !reader.IsDBNull(8) ? reader.GetDateTime(8) : (DateTime?)null,
                        Attempts = !reader.IsDBNull(9) ? reader.GetInt32(9) : 0
                    };
                    ret.Add(t);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error reading pending captcha tasks");
            }

            return ret;
        }

        // Mark a captcha task ignored
        public async Task MarkCaptchaIgnoredAsync(long id)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE dbo.CaptchaTasks
SET Status = N'ignored'
WHERE Id = @id;
IF OBJECT_ID(N'dbo.ChallengeFailureHistory',N'U') IS NOT NULL
 UPDATE dbo.ChallengeFailureHistory SET ResolutionStatus=N'ignored',ResolvedAt=SYSUTCDATETIME()
 WHERE CaptchaTaskId=@id AND ResolutionStatus=N'pending';
IF COL_LENGTH('dbo.CaptchaTasks', 'updated_at') IS NOT NULL
    EXEC sp_executesql N'UPDATE dbo.CaptchaTasks SET updated_at = SYSUTCDATETIME() WHERE Id = @taskId', N'@taskId BIGINT', @taskId = @id;
";
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error marking captcha ignored {Id}", id);
            }
        }

        // Save a captcha solution and optionally set a reuse expiry
        public async Task SaveCaptchaSolutionAsync(long id, string solutionJson, TimeSpan? reuse = null)
        {
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
UPDATE dbo.CaptchaTasks
SET SolutionJson = @sol, Status = N'solved', SolvedAt = SYSUTCDATETIME(), Attempts = ISNULL(Attempts, 0) + 1
WHERE Id = @id;
IF OBJECT_ID(N'dbo.ChallengeFailureHistory',N'U') IS NOT NULL
 UPDATE dbo.ChallengeFailureHistory SET ResolutionStatus=N'solved',ResolvedAt=SYSUTCDATETIME()
 WHERE CaptchaTaskId=@id AND ResolutionStatus=N'pending';
IF COL_LENGTH('dbo.CaptchaTasks', 'updated_at') IS NOT NULL
    EXEC sp_executesql N'UPDATE dbo.CaptchaTasks SET updated_at = SYSUTCDATETIME() WHERE Id = @taskId', N'@taskId BIGINT', @taskId = @id;
IF COL_LENGTH('dbo.CaptchaTasks', 'ReuseUntil') IS NOT NULL
    EXEC sp_executesql N'UPDATE dbo.CaptchaTasks SET ReuseUntil = @until WHERE Id = @taskId', N'@until DATETIME2, @taskId BIGINT', @until = @reuse, @taskId = @id;
";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@sol", (object?)solutionJson ?? DBNull.Value);
                if (reuse.HasValue)
                {
                    var reuseAt = DateTime.UtcNow + reuse.Value;
                    cmd.Parameters.AddWithValue("@reuse", reuseAt);
                }
                else
                {
                    cmd.Parameters.AddWithValue("@reuse", DBNull.Value);
                }

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error saving captcha solution for {Id}", id);
            }
        }

        // Richer overload of StoreCrawlResultAsync used by manual captcha operator
        public async Task StoreCrawlResultAsync(string url, int statusCode, string? text, DateTime whenUtc, string? userAgent, string? remoteIp, string? asn, string? source)
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
    UPDATE SET status_code = @status, content = @text, updated_at = @at
WHEN NOT MATCHED THEN
    INSERT (Url, status_code, content, created_at)
    VALUES (@url, @status, @text, @at);
";
                cmd.Parameters.AddWithValue("@url", url);
                cmd.Parameters.AddWithValue("@status", statusCode);
                cmd.Parameters.AddWithValue("@text", (object?)text ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@at", whenUtc);
                cmd.Parameters.AddWithValue("@ua", (object?)userAgent ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rip", (object?)remoteIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@asn", (object?)asn ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error storing crawl result for {Url}", url);
                throw;
            }
        }

        // Richer overload of UpsertVisitedUrlAsync used by manual captcha operator
        public async Task UpsertVisitedUrlAsync(string url, string? contentHashHex, int statusCode, DateTime whenUtc, string? userAgent, string? remoteIp, string? asn, string? source)
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
    UPDATE SET content_hash = @hash, status_code = @status, last_crawled = @at, updated_at = @at
WHEN NOT MATCHED THEN
    INSERT (url, content_hash, status_code, last_crawled)
    VALUES (@url, @hash, @status, @at);
";
                cmd.Parameters.AddWithValue("@url", url);
                cmd.Parameters.AddWithValue("@hash", (object?)contentHashHex ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@status", statusCode);
                cmd.Parameters.AddWithValue("@at", whenUtc);
                cmd.Parameters.AddWithValue("@ua", (object?)userAgent ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rip", (object?)remoteIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@asn", (object?)asn ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error upserting visited url {Url}", url);
            }
        }

        // Convenience overload to log error with current time
        public Task LogErrorAsync(string? url, string? errorText)
        {
            // Ensure the underlying non-null overload receives a non-null url to avoid null-reference warnings.
            return LogErrorAsync(url ?? string.Empty, errorText, DateTime.UtcNow);
        }
    }
}
