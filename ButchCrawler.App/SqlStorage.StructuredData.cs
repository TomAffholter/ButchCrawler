using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App;

public sealed record StructuredDataProvenance(
    string Source, string Format, string Category, long ContentBytes, long? RecordCount,
    int? TableCount, int? EntryCount, bool WasTruncated, string? Warning,
    string ContentHash, string? ParentSource = null);

public partial class SqlStorage
{
    public async Task EnsureStructuredDataSchemaAsync(CancellationToken ct = default)
    {
        const string sql = @"IF OBJECT_ID(N'dbo.StructuredDataSources',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StructuredDataSources(
  SourceHash BINARY(32) NOT NULL CONSTRAINT PK_StructuredDataSources PRIMARY KEY,
  Source NVARCHAR(2000) NOT NULL, Format NVARCHAR(40) NOT NULL, Category NVARCHAR(100) NOT NULL,
  ContentBytes BIGINT NOT NULL, RecordCount BIGINT NULL, TableCount INT NULL, EntryCount INT NULL,
  WasTruncated BIT NOT NULL CONSTRAINT DF_StructuredDataSources_WasTruncated DEFAULT 0,
  Warning NVARCHAR(2000) NULL, ContentHash CHAR(64) NOT NULL, ParentSource NVARCHAR(2000) NULL,
  ImportedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_StructuredDataSources_ImportedAtUtc DEFAULT SYSUTCDATETIME(), UpdatedAtUtc DATETIME2 NULL);
 CREATE INDEX IX_StructuredDataSources_Format ON dbo.StructuredDataSources(Format,ImportedAtUtc DESC);
END;";
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveStructuredDataProvenanceAsync(StructuredDataProvenance item, CancellationToken ct = default)
    {
        if (item.Source.Length > 2000) throw new InvalidDataException("Structured-data source exceeds the 2,000-character database limit.");
        await using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"MERGE dbo.StructuredDataSources WITH(HOLDLOCK) AS target
USING(SELECT @sourceHash SourceHash) AS incoming ON target.SourceHash=incoming.SourceHash
WHEN MATCHED THEN UPDATE SET Source=@source,Format=@format,Category=@category,ContentBytes=@bytes,
 RecordCount=@records,TableCount=@tables,EntryCount=@entries,WasTruncated=@truncated,Warning=@warning,
 ContentHash=@contentHash,ParentSource=@parent,UpdatedAtUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(SourceHash,Source,Format,Category,ContentBytes,RecordCount,TableCount,EntryCount,
 WasTruncated,Warning,ContentHash,ParentSource,ImportedAtUtc)
 VALUES(@sourceHash,@source,@format,@category,@bytes,@records,@tables,@entries,@truncated,@warning,@contentHash,@parent,SYSUTCDATETIME());";
        command.Parameters.Add("@sourceHash", SqlDbType.Binary, 32).Value = SHA256.HashData(Encoding.UTF8.GetBytes(item.Source));
        command.Parameters.Add("@source", SqlDbType.NVarChar, 2000).Value = item.Source;
        command.Parameters.Add("@format", SqlDbType.NVarChar, 40).Value = item.Format;
        command.Parameters.Add("@category", SqlDbType.NVarChar, 100).Value = item.Category;
        command.Parameters.Add("@bytes", SqlDbType.BigInt).Value = item.ContentBytes;
        command.Parameters.Add("@records", SqlDbType.BigInt).Value = (object?)item.RecordCount ?? DBNull.Value;
        command.Parameters.Add("@tables", SqlDbType.Int).Value = (object?)item.TableCount ?? DBNull.Value;
        command.Parameters.Add("@entries", SqlDbType.Int).Value = (object?)item.EntryCount ?? DBNull.Value;
        command.Parameters.Add("@truncated", SqlDbType.Bit).Value = item.WasTruncated;
        command.Parameters.Add("@warning", SqlDbType.NVarChar, 2000).Value = (object?)Limit(item.Warning, 2000) ?? DBNull.Value;
        command.Parameters.Add("@contentHash", SqlDbType.Char, 64).Value = item.ContentHash;
        command.Parameters.Add("@parent", SqlDbType.NVarChar, 2000).Value = (object?)Limit(item.ParentSource, 2000) ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? Limit(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];
}
