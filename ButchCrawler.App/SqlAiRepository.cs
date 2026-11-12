using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App;

public sealed class SqlAiRepository(string connectionString) : IAiRepository
{
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var lease = await AcquireAiWorkAsync("schema-v1", ct);
        await using var connection = await OpenAsync(ct);
        using var stream = typeof(SqlAiRepository).Assembly.GetManifestResourceStream("AiResearchSchema.sql")!;
        using var reader = new StreamReader(stream);
        using var command = connection.CreateCommand();
        command.CommandText = await reader.ReadToEndAsync(ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string Columns = "Id,Task,Source,InputHash,Model,OutputText,ResultJson,Reviewed,ReviewedText,CreatedAtUtc";
    private static AiSavedResult Read(SqlDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2),
        Convert.ToHexString((byte[])r[3]), r.GetString(4), r.GetString(5), r.GetString(6), r.GetBoolean(7),
        r.IsDBNull(8) ? null : r.GetString(8), DateTime.SpecifyKind(r.GetDateTime(9), DateTimeKind.Utc));
    private static byte[] SourceHash(string source) => SHA256.HashData(Encoding.Unicode.GetBytes(source));
    private static void CacheParameters(SqlCommand cmd, string task, string source, string hash, string model)
    {
        cmd.Parameters.AddWithValue("@task", task); cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.Add("@sh", SqlDbType.Binary, 32).Value = SourceHash(source);
        cmd.Parameters.Add("@ih", SqlDbType.Binary, 32).Value = Convert.FromHexString(hash);
        cmd.Parameters.AddWithValue("@model", model);
    }
    public async Task<AiSavedResult?> GetAiResultAsync(string task, string source, string hash, string model, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM dbo.AiAnalyses WHERE Task=@task AND SourceHash=@sh AND InputHash=@ih AND Model=@model";
        CacheParameters(cmd, task, source, hash, model);
        using var r = await cmd.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? Read(r) : null;
    }
    public async Task<AiSavedResult> SaveAiResultAsync(string task, string source, string hash, string model, string text, string json, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); using var cmd = connection.CreateCommand();
        cmd.CommandText = $"INSERT dbo.AiAnalyses(Task,Source,SourceHash,InputHash,Model,OutputText,ResultJson) OUTPUT {string.Join(",", Columns.Split(',').Select(c => "inserted." + c))} VALUES(@task,@source,@sh,@ih,@model,@text,@json)";
        CacheParameters(cmd, task, source, hash, model);
        cmd.Parameters.Add("@text", SqlDbType.NVarChar, -1).Value = text;
        cmd.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = json;
        using var r = await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct); return Read(r);
    }
    public async Task<IReadOnlyList<AiSavedResult>> HistoryAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT TOP (200) {Columns} FROM dbo.AiAnalyses ORDER BY CreatedAtUtc DESC,Id DESC";
        using var r = await cmd.ExecuteReaderAsync(ct); var list = new List<AiSavedResult>();
        while (await r.ReadAsync(ct)) list.Add(Read(r)); return list;
    }
    public async Task<bool> ConsumeAiRequestAsync(int dailyLimit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct); using var cmd = connection.CreateCommand();
        cmd.CommandText = @"SET XACT_ABORT ON; BEGIN TRAN;
IF NOT EXISTS(SELECT 1 FROM dbo.AiRequestUsage WITH(UPDLOCK,HOLDLOCK) WHERE DateUtc=CONVERT(date,SYSUTCDATETIME()))
 INSERT dbo.AiRequestUsage VALUES(CONVERT(date,SYSUTCDATETIME()),0);
UPDATE dbo.AiRequestUsage SET Requests=Requests+1 WHERE DateUtc=CONVERT(date,SYSUTCDATETIME()) AND Requests<@limit;
SELECT @@ROWCOUNT; COMMIT;";
        cmd.Parameters.AddWithValue("@limit", dailyLimit); return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }
    public async Task<IAsyncDisposable> AcquireAiWorkAsync(string key, CancellationToken ct)
    {
        var connection = await OpenAsync(ct);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=15000; SELECT @r;";
            cmd.Parameters.AddWithValue("@key", "ButchCrawlerAI:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) < 0) throw new AiResearchException("Another AI request is still working on this source. Try again shortly.");
            return new Lease(connection, (string)cmd.Parameters["@key"].Value);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private sealed class Lease(SqlConnection connection, string key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { using var cmd = connection.CreateCommand(); cmd.CommandText = "EXEC sys.sp_releaseapplock @Resource=@key,@LockOwner='Session'"; cmd.Parameters.AddWithValue("@key", key); await cmd.ExecuteNonQueryAsync(); }
            finally { await connection.DisposeAsync(); }
        }
    }

    private static readonly Dictionary<string, (string Table, string Source, string Text)> Tables = new()
    {
        ["crawl"] = ("CrawlResults", "Url", "content"),
        ["structured"] = ("CrawlResults", "Url", "content"),
        ["document"] = ("documents", "url", "text"),
        ["audio"] = ("AudioTranscripts", "Source", "Transcript")
    };
    public static string[] SearchTerms(string query) => Regex.Matches(query, @"[\p{L}\p{N}]{2,}").Select(m => m.Value)
        .Where(t => !new[] { "what", "when", "where", "which", "who", "the", "and", "are", "was", "were", "does", "did", "about", "with", "from", "that", "this", "say", "sources", "saved", "do", "of", "in", "to", "is", "on" }.Contains(t.ToLowerInvariant()))
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
    public async Task<IReadOnlyList<AiDocument>> SearchAsync(string query, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        using var structuredCheck = connection.CreateCommand(); structuredCheck.CommandText = "SELECT CASE WHEN OBJECT_ID(N'dbo.StructuredDataSources',N'U') IS NULL THEN 0 ELSE 1 END";
        var structuredExists = Convert.ToInt32(await structuredCheck.ExecuteScalarAsync(ct)) == 1;
        var selects = new List<string>();
        foreach (var (kind, t) in Tables)
        {
            if (kind == "structured" && !structuredExists) continue;
            using var exists = connection.CreateCommand(); exists.CommandText = "SELECT OBJECT_ID(@table,'U')"; exists.Parameters.AddWithValue("@table", "dbo." + t.Table);
            if (await exists.ExecuteScalarAsync(ct) is DBNull) continue;
            var usable = kind == "audio" ? " AND IsComplete=1" : kind is "crawl" or "structured" ? " AND status_code BETWEEN 200 AND 299" : "";
            if (structuredExists && kind == "crawl") usable += $" AND NOT EXISTS(SELECT 1 FROM dbo.StructuredDataSources sd WHERE sd.Source=[{t.Source}] COLLATE Latin1_General_100_BIN2)";
            if (kind == "structured") usable += $" AND EXISTS(SELECT 1 FROM dbo.StructuredDataSources sd WHERE sd.Source=[{t.Source}] COLLATE Latin1_General_100_BIN2)";
            selects.Add($"SELECT N'{kind}' Kind, [{t.Source}] Source, [{t.Text}] Body FROM dbo.[{t.Table}] WHERE NULLIF([{t.Text}],N'') IS NOT NULL{usable}");
        }
        if (selects.Count == 0) return Array.Empty<AiDocument>();
        using var cmd = connection.CreateCommand(); var plan = CollectionSearchQuery.Parse(query); var terms = plan.Terms;
        var matches = plan.PositiveTermIndexes.Select(i => $"CASE WHEN CHARINDEX(@t{i},Body)>0 OR CHARINDEX(@t{i},Source)>0 THEN 1 ELSE 0 END").ToArray();
        var score = matches.Length == 0 ? "0" : string.Join("+", matches);
        var previewIndex = plan.PositiveTermIndexes.FirstOrDefault(-1);
        var preview = previewIndex < 0 ? "LEFT(Body,500)" : $"SUBSTRING(Body,CASE WHEN CHARINDEX(@t{previewIndex},Body)>200 THEN CHARINDEX(@t{previewIndex},Body)-200 ELSE 1 END,500)";
        cmd.CommandText = $"WITH sources AS ({string.Join(" UNION ALL ", selects)}), scored AS (SELECT *,({score}) AS Relevance FROM sources) SELECT TOP(100) Source,Kind,LEN(Body),{preview} FROM scored WHERE {plan.SqlPredicate} ORDER BY Relevance DESC,Source,Kind";
        for (var i = 0; i < terms.Count; i++) cmd.Parameters.AddWithValue("@t" + i, terms[i]);
        using var r = await cmd.ExecuteReaderAsync(ct); var list = new List<AiDocument>();
        while (await r.ReadAsync(ct)) list.Add(new(r.GetString(0), r.GetString(1), checked((int)Convert.ToInt64(r.GetValue(2))), r.GetString(3)));
        return list;
    }
    public async Task<string> ReadDocumentAsync(string source, string kind, CancellationToken ct)
    {
        if (!Tables.TryGetValue(kind, out var t)) throw new AiResearchException("Unknown source type.");
        await using var connection = await OpenAsync(ct); using var cmd = connection.CreateCommand();
        var usable = kind == "audio" ? " AND IsComplete=1" : kind is "crawl" or "structured" ? " AND status_code BETWEEN 200 AND 299" : "";
        var order = kind == "audio" ? "TranscribedAtUtc DESC,Id DESC" : "LEN([" + t.Text + "]) DESC";
        cmd.CommandText = $"SELECT TOP(1) [{t.Text}] FROM dbo.[{t.Table}] WHERE [{t.Source}] COLLATE Latin1_General_100_BIN2=@source COLLATE Latin1_General_100_BIN2{usable} ORDER BY {order}";
        cmd.Parameters.AddWithValue("@source", source);
        return (await cmd.ExecuteScalarAsync(ct)) as string ?? throw new AiResearchException("This source has no saved text.");
    }
}
