using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App
{
    public partial class SqlStorage
    {
        public async Task UpsertCrawlArtifactAsync(
            string url,
            string? finalUrl,
            string? title,
            string? rawHtml,
            string? jsonLd,
            string? openGraphJson,
            string? microformatsJson,
            string? outgoingLinksJson,
            string? contentHash,
            string? fetchHeadersJson,
            string? redirectChainJson,
            int? statusCode,
            string? userAgent,
            string? remoteIp,
            string? asn,
            string? source,
            DateTime retrievedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
MERGE dbo.CrawlArtifacts AS target
USING (SELECT @url AS Url) AS src
    ON target.Url = src.Url
WHEN MATCHED THEN
    UPDATE SET FinalUrl = @finalUrl,
               Title = @title,
               RawHtml = @rawHtml,
               JsonLd = @jsonLd,
               OpenGraphJson = @openGraph,
               MicroformatsJson = @microformats,
               OutgoingLinksJson = @outgoingLinks,
               ContentHash = @contentHash,
               FetchHeadersJson = @fetchHeaders,
               RedirectChainJson = @redirectChain,
               StatusCode = @statusCode,
               UserAgent = @userAgent,
               RemoteIp = @remoteIp,
               Asn = @asn,
               Source = @source,
               RetrievedAt = @retrievedAt,
               UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (Url, FinalUrl, Title, RawHtml, JsonLd, OpenGraphJson, MicroformatsJson, OutgoingLinksJson, ContentHash, FetchHeadersJson, RedirectChainJson, StatusCode, UserAgent, RemoteIp, Asn, Source, RetrievedAt, CreatedAt)
    VALUES (@url, @finalUrl, @title, @rawHtml, @jsonLd, @openGraph, @microformats, @outgoingLinks, @contentHash, @fetchHeaders, @redirectChain, @statusCode, @userAgent, @remoteIp, @asn, @source, @retrievedAt, SYSUTCDATETIME());
";
                cmd.Parameters.AddWithValue("@url", url);
                cmd.Parameters.AddWithValue("@finalUrl", (object?)finalUrl ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rawHtml", (object?)rawHtml ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@jsonLd", (object?)jsonLd ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@openGraph", (object?)openGraphJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@microformats", (object?)microformatsJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@outgoingLinks", (object?)outgoingLinksJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@contentHash", (object?)contentHash ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fetchHeaders", (object?)fetchHeadersJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@redirectChain", (object?)redirectChainJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@statusCode", (object?)statusCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@userAgent", (object?)userAgent ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@remoteIp", (object?)remoteIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@asn", (object?)asn ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@retrievedAt", retrievedAtUtc);

                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DB error upserting crawl artifact for {Url}", url);
            }
        }
    }
}
