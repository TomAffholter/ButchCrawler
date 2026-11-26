using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App
{
    public sealed record PdfRelevanceSpan(string Token, string Category, int StartIndex, int Length, double Score, string? Snippet);

    public partial class SqlStorage
    {
        public async Task<long> AppendCrawlCaptureIfChangedAsync(
            string url,
            string? finalUrl,
            string captureType,
            int? statusCode,
            string? bodyText,
            string? bodyTextFull,
            string? title,
            string? contentHash,
            string? etag,
            string? lastModified,
            bool wasNotModified,
            string? userAgent,
            string? remoteIp,
            string? asn,
            string? source,
            DateTime retrievedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentNullException(nameof(url));

            // Match the fixed-width capture columns. Provenance strings can include
            // seed tags and robots decisions and previously caused an otherwise valid
            // capture to be rejected when they exceeded the SQL column width.
            finalUrl = LimitCaptureValue(finalUrl, 2000);
            captureType = LimitCaptureValue(captureType, 64) ?? "http";
            title = LimitCaptureValue(title, 1024);
            contentHash = LimitCaptureValue(contentHash, 128);
            etag = LimitCaptureValue(etag, 512);
            lastModified = LimitCaptureValue(lastModified, 256);
            userAgent = LimitCaptureValue(userAgent, 512);
            remoteIp = LimitCaptureValue(remoteIp, 64);
            asn = LimitCaptureValue(asn, 128);
            source = LimitCaptureValue(source, 512);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            var hasCaptures = await TableExistsAsync(conn, "dbo", "CrawlCaptures").ConfigureAwait(false);
            if (!hasCaptures)
            {
                // Migration not yet applied: keep compatibility by signaling no capture row.
                return 0;
            }

            var hasHash = !string.IsNullOrWhiteSpace(contentHash);
            if (hasHash)
            {
                using var prevCmd = conn.CreateCommand();
                prevCmd.CommandText = @"
SELECT TOP (1) ContentHash
FROM dbo.CrawlCaptures
WHERE Url = @url
ORDER BY RetrievedAt DESC, Id DESC;
";
                prevCmd.Parameters.AddWithValue("@url", url);
                var prev = await prevCmd.ExecuteScalarAsync().ConfigureAwait(false) as string;

                if (!string.IsNullOrWhiteSpace(prev) && string.Equals(prev, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
            }

            var compressed = string.Equals(captureType, "html", StringComparison.OrdinalIgnoreCase) &&
                await HasCompressedHtmlSchemaAsync(conn).ConfigureAwait(false);
            using var transaction = compressed ? conn.BeginTransaction() : null;
            var blobId = transaction is not null
                ? await StoreCrawlHtmlBlobAsync(conn, transaction, bodyTextFull).ConfigureAwait(false)
                : null;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            var blobColumn = compressed ? ", RawHtmlBlobId" : "";
            var blobValue = compressed ? ", @blobId" : "";
            cmd.CommandText = $@"
INSERT INTO dbo.CrawlCaptures
(
    Url, FinalUrl, CaptureType, StatusCode, BodyText, BodyTextFull, Title,
    ContentHash, ETag, LastModified, WasNotModified,
    UserAgent, RemoteIp, Asn, Source, RetrievedAt{blobColumn}
)
VALUES
(
    @url, @finalUrl, @captureType, @statusCode, @bodyText, @bodyTextFull, @title,
    @contentHash, @etag, @lastModified, @wasNotModified,
    @userAgent, @remoteIp, @asn, @source, @retrievedAt{blobValue}
);
SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
";

            cmd.Parameters.AddWithValue("@url", url);
            cmd.Parameters.AddWithValue("@finalUrl", (object?)finalUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@captureType", (object?)captureType ?? "http");
            cmd.Parameters.AddWithValue("@statusCode", (object?)statusCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@bodyText", (object?)bodyText ?? DBNull.Value);
            cmd.Parameters.Add("@bodyTextFull", SqlDbType.NVarChar, -1).Value = compressed ? DBNull.Value : (object?)bodyTextFull ?? DBNull.Value;
            if (compressed) cmd.Parameters.Add("@blobId", SqlDbType.BigInt).Value = (object?)blobId ?? DBNull.Value;
            cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@contentHash", (object?)contentHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@etag", (object?)etag ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lastModified", (object?)lastModified ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@wasNotModified", wasNotModified ? 1 : 0);
            cmd.Parameters.AddWithValue("@userAgent", (object?)userAgent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@remoteIp", (object?)remoteIp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@asn", (object?)asn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@retrievedAt", retrievedAtUtc);

            var idObj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync().ConfigureAwait(false);
            return idObj == null || idObj == DBNull.Value ? 0 : Convert.ToInt64(idObj);
        }

        private static string? LimitCaptureValue(string? value, int maxLength)
            => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];

        public async Task SavePdfRelevanceAnnotationsAsync(long captureId, IEnumerable<PdfRelevanceSpan>? spans)
        {
            if (captureId <= 0 || spans == null) return;

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            var hasTable = await TableExistsAsync(conn, "dbo", "PdfRelevanceAnnotations").ConfigureAwait(false);
            if (!hasTable) return;

            using var tran = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tran;
                cmd.CommandText = @"
INSERT INTO dbo.PdfRelevanceAnnotations
(CaptureId, MatchToken, Category, StartIndex, MatchLength, Score, Snippet)
VALUES
(@captureId, @token, @category, @startIndex, @matchLength, @score, @snippet);
";

                var pCaptureId = cmd.CreateParameter(); pCaptureId.ParameterName = "@captureId"; cmd.Parameters.Add(pCaptureId);
                var pToken = cmd.CreateParameter(); pToken.ParameterName = "@token"; cmd.Parameters.Add(pToken);
                var pCategory = cmd.CreateParameter(); pCategory.ParameterName = "@category"; cmd.Parameters.Add(pCategory);
                var pStart = cmd.CreateParameter(); pStart.ParameterName = "@startIndex"; cmd.Parameters.Add(pStart);
                var pLen = cmd.CreateParameter(); pLen.ParameterName = "@matchLength"; cmd.Parameters.Add(pLen);
                var pScore = cmd.CreateParameter(); pScore.ParameterName = "@score"; cmd.Parameters.Add(pScore);
                var pSnippet = cmd.CreateParameter(); pSnippet.ParameterName = "@snippet"; cmd.Parameters.Add(pSnippet);

                foreach (var span in spans)
                {
                    if (span == null || string.IsNullOrWhiteSpace(span.Token) || span.Length <= 0) continue;
                    pCaptureId.Value = captureId;
                    pToken.Value = span.Token;
                    pCategory.Value = span.Category;
                    pStart.Value = span.StartIndex;
                    pLen.Value = span.Length;
                    pScore.Value = span.Score;
                    pSnippet.Value = (object?)span.Snippet ?? DBNull.Value;
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

        public async Task<(string? ContentHash, string? ETag, string? LastModified, DateTime? RetrievedAt)> GetLatestCaptureMetadataAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return (null, null, null, null);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);

            var hasCaptures = await TableExistsAsync(conn, "dbo", "CrawlCaptures").ConfigureAwait(false);
            if (!hasCaptures) return (null, null, null, null);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT TOP (1) ContentHash, ETag, LastModified, RetrievedAt
FROM dbo.CrawlCaptures
WHERE Url = @url
ORDER BY RetrievedAt DESC, Id DESC;
";
            cmd.Parameters.AddWithValue("@url", url);

            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false)) return (null, null, null, null);

            var hash = reader.IsDBNull(0) ? null : reader.GetString(0);
            var etag = reader.IsDBNull(1) ? null : reader.GetString(1);
            var lastModified = reader.IsDBNull(2) ? null : reader.GetString(2);
            var retrieved = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
            return (hash, etag, lastModified, retrieved);
        }

        public async Task TryAppendCaptureAndAnnotationsAsync(
            string url,
            string? finalUrl,
            string captureType,
            int? statusCode,
            string? bodyText,
            string? bodyTextFull,
            string? title,
            string? contentHash,
            string? etag,
            string? lastModified,
            bool wasNotModified,
            string? userAgent,
            string? remoteIp,
            string? asn,
            string? source,
            DateTime retrievedAtUtc,
            IEnumerable<PdfRelevanceSpan>? spans)
        {
            try
            {
                var captureId = await AppendCrawlCaptureIfChangedAsync(
                    url,
                    finalUrl,
                    captureType,
                    statusCode,
                    bodyText,
                    bodyTextFull,
                    title,
                    contentHash,
                    etag,
                    lastModified,
                    wasNotModified,
                    userAgent,
                    remoteIp,
                    asn,
                    source,
                    retrievedAtUtc).ConfigureAwait(false);

                if (captureId > 0)
                {
                    await SavePdfRelevanceAnnotationsAsync(captureId, spans).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to append immutable crawl capture for {Url}", url);
            }
        }

        private static async Task<bool> TableExistsAsync(SqlConnection conn, string schema, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE s.name = @schema AND t.name = @table
) THEN 1 ELSE 0 END;
";
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);
            var obj = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
            return obj != null && obj != DBNull.Value && Convert.ToInt32(obj) == 1;
        }
    }
}
