using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public partial class SqlStorage
    {
        public async Task<string?> GetCachedPdfTextAsync(string url, byte[] pdfHash)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT ExtractedText
                FROM dbo.PdfResults
                WHERE Url = @url
                  AND (ExtractedHash = @exHash OR @exHash IS NULL);
            ";
            cmd.Parameters.AddWithValue("@url", url);
            cmd.Parameters.AddWithValue("@exHash", (object?)pdfHash ?? DBNull.Value);

            try
            {
                var res = await cmd.ExecuteScalarAsync();
                return res == DBNull.Value || res == null ? null : (string)res;
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "DB error reading PdfResults for {Url}", url);
                throw;
            }
        }

        public async Task UpsertPdfTextAsync(string url, byte[] pdfHash, long? contentLength, string? contentType, string extractedText, byte[] extractedHash, bool isScanned, string? source = null)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
MERGE dbo.PdfResults AS target
USING (SELECT @url AS Url) AS src
    ON target.Url = src.Url
WHEN MATCHED THEN
    UPDATE SET PdfHash = @pdfHash, ContentLength = @len, ContentType = @ctype, ExtractedText = @text,
               ExtractedHash = @eHash, IsScanned = @isScanned, ExtractedAt = SYSUTCDATETIME(), Source = @source
WHEN NOT MATCHED THEN
    INSERT (Url, PdfHash, ContentLength, ContentType, ExtractedText, ExtractedHash, IsScanned, Source)
    VALUES (@url, @pdfHash, @len, @ctype, @text, @eHash, @isScanned, @source);";
            cmd.Parameters.AddWithValue("@url", url);
            cmd.Parameters.AddWithValue("@pdfHash", (object?)pdfHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@len", contentLength.HasValue ? (object)contentLength.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@ctype", (object?)contentType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@text", (object?)extractedText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@eHash", (object?)extractedHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isScanned", isScanned);
            cmd.Parameters.AddWithValue("@source", (object?)source ?? DBNull.Value);

            try
            {
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, "DB error upserting PdfResults for {Url}", url);
                throw;
            }
        }
    }
}