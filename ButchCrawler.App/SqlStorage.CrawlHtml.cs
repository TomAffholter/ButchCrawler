using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App;

public partial class SqlStorage
{
    // A schema upgrade is performed with the crawler stopped, so this is stable for a run.
    private int _compressedHtmlSchema;

    private async Task<bool> HasCompressedHtmlSchemaAsync(SqlConnection connection)
    {
        var known = Volatile.Read(ref _compressedHtmlSchema);
        if (known != 0) return known > 0;

        using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT CASE WHEN OBJECT_ID(N'dbo.StoreCrawlHtmlBlob', N'P') IS NOT NULL
    AND OBJECT_ID(N'dbo.CrawlHtmlBlobs', N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.CrawlArtifacts', N'RawHtmlBlobId') IS NOT NULL
    AND COL_LENGTH(N'dbo.CrawlCaptures', N'RawHtmlBlobId') IS NOT NULL
    THEN 1 ELSE -1 END;";
        var detected = Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
        if (Interlocked.CompareExchange(ref _compressedHtmlSchema, detected, 0) == 0 && detected < 0)
            _logger.LogWarning("Compressed HTML storage is unavailable. Apply schema script 019 with the crawler stopped to enable it.");
        return detected > 0;
    }

    private static async Task<long?> StoreCrawlHtmlBlobAsync(SqlConnection connection, SqlTransaction transaction, string? html)
    {
        if (html is null) return null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo.StoreCrawlHtmlBlob";
        command.Parameters.Add("@Html", SqlDbType.NVarChar, -1).Value = html;
        var id = command.Parameters.Add("@BlobId", SqlDbType.BigInt);
        id.Direction = ParameterDirection.Output;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return id.Value is long value
            ? value
            : throw new InvalidOperationException("Compressed HTML storage returned no reference.");
    }
}
