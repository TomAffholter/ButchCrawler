using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App;

public partial class SqlStorage : IAudioTranscriptStore
{
    public async Task<int> RequeueUnprocessedAudioAsync(CancellationToken ct)
    {
        using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        var suffixes = AudioProcessor.Extensions.Select((_, index) => $"LOWER(LEFT(q.Url,CHARINDEX('?',q.Url+'?')-1)) LIKE @extension{index}");
        command.CommandText = @"UPDATE q SET Status=N'pending',locked_until=NULL,LockToken=NULL,updated_at=SYSUTCDATETIME()
FROM dbo.CrawlQueue q WHERE q.Status=N'completed' AND (" + string.Join(" OR ", suffixes) + @")
AND NOT EXISTS(SELECT 1 FROM dbo.VisitedUrls v WHERE v.url COLLATE Latin1_General_100_BIN2=q.Url COLLATE Latin1_General_100_BIN2)
AND NOT EXISTS(SELECT 1 FROM dbo.AudioTranscripts a WHERE a.SourceHash=HASHBYTES('SHA2_256',CONVERT(VARBINARY(MAX),q.Url)) AND a.IsComplete=1);";
        for (var i = 0; i < AudioProcessor.Extensions.Length; i++) command.Parameters.AddWithValue("@extension" + i, "%" + AudioProcessor.Extensions[i]);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<AudioTranscriptSummary>> SearchAudioTranscriptsAsync(string search, bool keywordMatchesOnly, CancellationToken ct)
    {
        using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"WITH completed AS (
 SELECT *,ROW_NUMBER() OVER(PARTITION BY SourceHash ORDER BY TranscribedAtUtc DESC,Id DESC) AS revision
 FROM dbo.AudioTranscripts WHERE IsComplete=1
)
SELECT TOP(200) t.Id,t.Source,COALESCE(matches.Keywords,N''),t.TranscribedAtUtc,LEFT(t.Transcript,350)
FROM completed t OUTER APPLY (
 SELECT STRING_AGG(CONVERT(NVARCHAR(MAX),k.Keyword),N', ') AS Keywords
 FROM dbo.SeedKeywords k WHERE k.IsActive=1 AND LEN(LTRIM(RTRIM(k.Keyword)))>0 AND CHARINDEX(k.Keyword,t.Transcript COLLATE Latin1_General_100_CI_AS)>0
) matches
WHERE t.revision=1 AND (@matched=0 OR matches.Keywords IS NOT NULL)
AND (@search=N'' OR CHARINDEX(@search,t.Transcript COLLATE Latin1_General_100_CI_AS)>0 OR CHARINDEX(@search,t.Source)>0 OR CHARINDEX(@search,matches.Keywords)>0)
ORDER BY t.TranscribedAtUtc DESC,t.Id DESC;";
        command.Parameters.AddWithValue("@search", search.Trim()); command.Parameters.AddWithValue("@matched", keywordMatchesOnly);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<AudioTranscriptSummary>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3), reader.GetString(4)));
        return rows;
    }

    public async Task<string?> GetAudioTranscriptTextAsync(long id, CancellationToken ct)
    {
        using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand(); command.CommandText = "SELECT Transcript FROM dbo.AudioTranscripts WHERE Id=@id AND IsComplete=1";
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task<AudioTranscript?> GetAudioTranscriptAsync(string source, string contentHash, string model, CancellationToken ct)
    {
        using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"IF OBJECT_ID(N'dbo.AudioTranscripts',N'U') IS NULL THROW 51030, 'Run Scripts/012_CreateAudioTranscripts.sql before using audio transcription.', 1;
SELECT Transcript,SegmentsJson,IsComplete,ChunkSeconds FROM dbo.AudioTranscripts WHERE SourceHash=HASHBYTES('SHA2_256',CONVERT(VARBINARY(MAX),@source)) AND ContentHash=@hash AND Model=@model;";
        command.Parameters.AddWithValue("@source", source); command.Parameters.AddWithValue("@hash", Convert.FromHexString(contentHash)); command.Parameters.AddWithValue("@model", model);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new AudioTranscript(source, contentHash, model, reader.GetString(0), JsonSerializer.Deserialize<List<AudioSegment>>(reader.GetString(1)) ?? new(), reader.GetBoolean(2), reader.GetInt32(3)) : null;
    }

    public async Task SaveAudioTranscriptAsync(AudioTranscript transcript, CancellationToken ct)
    {
        using var connection = new SqlConnection(_connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = @"UPDATE dbo.AudioTranscripts WITH (UPDLOCK,SERIALIZABLE) SET Transcript=@text,SegmentsJson=@segments,TranscribedAtUtc=SYSUTCDATETIME(),Source=@source,IsComplete=@complete,ChunkSeconds=@chunkSeconds
WHERE SourceHash=HASHBYTES('SHA2_256',CONVERT(VARBINARY(MAX),@source)) AND ContentHash=@hash AND Model=@model;
IF @@ROWCOUNT=0 INSERT dbo.AudioTranscripts(Source,SourceHash,ContentHash,Model,Transcript,SegmentsJson,IsComplete,ChunkSeconds)
VALUES(@source,HASHBYTES('SHA2_256',CONVERT(VARBINARY(MAX),@source)),@hash,@model,@text,@segments,@complete,@chunkSeconds);";
        command.Parameters.AddWithValue("@source", transcript.Source); command.Parameters.AddWithValue("@hash", Convert.FromHexString(transcript.ContentHash));
        command.Parameters.AddWithValue("@model", transcript.Model); command.Parameters.AddWithValue("@text", transcript.Text);
        command.Parameters.AddWithValue("@segments", JsonSerializer.Serialize(transcript.Segments));
        command.Parameters.AddWithValue("@complete", transcript.IsComplete); command.Parameters.AddWithValue("@chunkSeconds", transcript.ChunkSeconds);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false); transaction.Commit();
    }
}
