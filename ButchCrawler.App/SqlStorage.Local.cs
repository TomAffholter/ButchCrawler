using Microsoft.Data.SqlClient;
using System.Data;

namespace ButchCrawler.App;

public partial class SqlStorage
{
    // Text and keyword matches commit together before the UI reports success.
    public async Task SaveLocalTextAsync(string file, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("No readable text was extracted; the previous saved result was retained.");
        if (file.Length > 2000) throw new InvalidDataException("File path exceeds the database's 2,000-character source limit.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"MERGE dbo.CrawlResults WITH (HOLDLOCK) AS target
USING (SELECT @url AS Url) AS source ON target.Url=source.Url
WHEN MATCHED THEN UPDATE SET status_code=200,content=@text,updated_at=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT(Url,status_code,content,created_at,updated_at) VALUES(@url,200,@text,SYSUTCDATETIME(),SYSUTCDATETIME());";
            command.Parameters.Add("@url", SqlDbType.NVarChar, 2000).Value = file;
            command.Parameters.Add("@text", SqlDbType.NVarChar, -1).Value = text;
            await command.ExecuteNonQueryAsync(ct);
            await SaveKeywordHitsInTransactionAsync(connection, transaction, file, text);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch { try { transaction.Rollback(); } catch { } throw; }
    }
}
