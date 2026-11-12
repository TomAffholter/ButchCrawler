using Microsoft.Data.SqlClient;

namespace ButchCrawler.App;

public sealed record HistoricalNewspaperMatch(string Url, string Keyword, string State,
    string Title, string? PublicationDate, string Snippet, string Source = "Library of Congress");

public partial class SqlStorage
{
    public async Task EnsureHistoricalNewspaperSchemaAsync(CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
IF OBJECT_ID(N'dbo.HistoricalNewspaperSearches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HistoricalNewspaperSearches(
        QueryKey char(64) NOT NULL CONSTRAINT PK_HistoricalNewspaperSearches PRIMARY KEY,
        Keyword nvarchar(500) NOT NULL,
        States nvarchar(2000) NOT NULL,
        LastSearchedAt datetime2(0) NOT NULL,
        ResultsFound int NOT NULL CONSTRAINT DF_HistoricalNewspaperSearches_Found DEFAULT(0),
        ResultsQueued int NOT NULL CONSTRAINT DF_HistoricalNewspaperSearches_Queued DEFAULT(0),
        Status nvarchar(30) NOT NULL,
        Error nvarchar(2000) NULL
    );
END;

IF OBJECT_ID(N'dbo.HistoricalNewspaperMatches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HistoricalNewspaperMatches(
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HistoricalNewspaperMatches PRIMARY KEY,
        Url nvarchar(2000) NOT NULL,
        UrlHash binary(32) NOT NULL,
        Keyword nvarchar(500) NOT NULL,
        KeywordHash binary(32) NOT NULL,
        State nvarchar(100) NOT NULL,
        Title nvarchar(1000) NULL,
        PublicationDate nvarchar(40) NULL,
        Snippet nvarchar(max) NULL,
        Source nvarchar(100) NOT NULL,
        DiscoveredAt datetime2(0) NOT NULL CONSTRAINT DF_HistoricalNewspaperMatches_Discovered DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_HistoricalNewspaperMatches UNIQUE(UrlHash, KeywordHash)
    );
    CREATE INDEX IX_HistoricalNewspaperMatches_UrlHash ON dbo.HistoricalNewspaperMatches(UrlHash);
END;
""";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> ShouldSearchHistoricalNewspapersAsync(string queryKey, int refreshDays, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT CASE WHEN EXISTS(
    SELECT 1 FROM dbo.HistoricalNewspaperSearches
    WHERE QueryKey=@key AND Status=N'completed'
      AND LastSearchedAt >= DATEADD(day, -@days, SYSUTCDATETIME())
) THEN 0 ELSE 1 END;
""";
        command.Parameters.AddWithValue("@key", queryKey);
        command.Parameters.AddWithValue("@days", Math.Clamp(refreshDays, 1, 365));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 1;
    }

    public async Task<int> SaveHistoricalNewspaperMatchesAsync(IReadOnlyList<HistoricalNewspaperMatch> matches, CancellationToken ct = default)
    {
        if (matches.Count == 0) return 0;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var inserted = 0;
        foreach (var match in matches)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
IF NOT EXISTS(SELECT 1 FROM dbo.HistoricalNewspaperMatches
              WHERE UrlHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@url))
                AND KeywordHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@keyword)))
BEGIN
    INSERT dbo.HistoricalNewspaperMatches(Url,UrlHash,Keyword,KeywordHash,State,Title,PublicationDate,Snippet,Source)
    VALUES(@url,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@url)),@keyword,
           HASHBYTES('SHA2_256',CONVERT(varbinary(max),@keyword)),@state,@title,@date,@snippet,@source);
    SELECT 1;
END
ELSE SELECT 0;
""";
            command.Parameters.AddWithValue("@url", match.Url);
            command.Parameters.AddWithValue("@keyword", match.Keyword);
            command.Parameters.AddWithValue("@state", match.State);
            command.Parameters.AddWithValue("@title", (object?)match.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("@date", (object?)match.PublicationDate ?? DBNull.Value);
            command.Parameters.AddWithValue("@snippet", (object?)match.Snippet ?? DBNull.Value);
            command.Parameters.AddWithValue("@source", match.Source);
            var wasInserted = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
            inserted += wasInserted;
            if (wasInserted == 1)
            {
                await using var searchable = connection.CreateCommand();
                searchable.Transaction = transaction;
                searchable.CommandText = """
IF NOT EXISTS(SELECT 1 FROM dbo.CrawlResults WHERE Url=@url)
    INSERT dbo.CrawlResults(Url,status_code,content,created_at)
    VALUES(@url,200,@content,SYSUTCDATETIME());
ELSE
    UPDATE dbo.CrawlResults SET content=@content,updated_at=SYSUTCDATETIME()
    WHERE Url=@url AND (content IS NULL OR LEN(content)=0);
""";
                searchable.Parameters.AddWithValue("@url", match.Url);
                searchable.Parameters.AddWithValue("@content",
                    $"[Historical newspaper match]\nTitle: {match.Title}\nDate: {match.PublicationDate ?? "Unknown"}\nState: {match.State}\nMatched keyword: {match.Keyword}\nSource: {match.Source}\n{match.Snippet}".Trim());
                await searchable.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return inserted;
    }

    public async Task SaveHistoricalNewspaperSearchAsync(string queryKey, string keyword, string states,
        int found, int queued, string status, string? error, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
MERGE dbo.HistoricalNewspaperSearches AS target
USING (SELECT @key AS QueryKey) AS source ON target.QueryKey=source.QueryKey
WHEN MATCHED THEN UPDATE SET Keyword=@keyword,States=@states,LastSearchedAt=SYSUTCDATETIME(),
    ResultsFound=@found,ResultsQueued=@queued,Status=@status,Error=@error
WHEN NOT MATCHED THEN INSERT(QueryKey,Keyword,States,LastSearchedAt,ResultsFound,ResultsQueued,Status,Error)
    VALUES(@key,@keyword,@states,SYSUTCDATETIME(),@found,@queued,@status,@error);
""";
        command.Parameters.AddWithValue("@key", queryKey);
        command.Parameters.AddWithValue("@keyword", keyword);
        command.Parameters.AddWithValue("@states", states);
        command.Parameters.AddWithValue("@found", found);
        command.Parameters.AddWithValue("@queued", queued);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetHistoricalNewspaperMatchTextAsync(string url, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT Title,PublicationDate,State,Keyword,Snippet,Source
FROM dbo.HistoricalNewspaperMatches
WHERE UrlHash=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@url)) AND Url=@url
ORDER BY Id;
""";
        command.Parameters.AddWithValue("@url", url);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var blocks = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var title = reader.IsDBNull(0) ? "Historical newspaper result" : reader.GetString(0);
            var date = reader.IsDBNull(1) ? null : reader.GetString(1);
            var state = reader.GetString(2);
            var keyword = reader.GetString(3);
            var snippet = reader.IsDBNull(4) ? null : reader.GetString(4);
            var source = reader.GetString(5);
            blocks.Add($"[Historical newspaper match]\nTitle: {title}\nDate: {date ?? "Unknown"}\nState: {state}\nMatched keyword: {keyword}\nSource: {source}\n{snippet}".Trim());
        }
        return blocks.Count == 0 ? null : string.Join("\n\n", blocks);
    }
}
