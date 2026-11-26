using System.IO;
using ButchCrawler.Operator;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static class ResetIntegrationTests
{
    public static async Task RunAsync()
    {
        var settings = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App"))
            .AddJsonFile("appsettings.json").AddEnvironmentVariables().Build();
        var source = new SqlConnectionStringBuilder(settings.GetConnectionString("DefaultConnection")) { ConnectTimeout = 5 };
        await using (var original = new SqlConnection(source.ConnectionString))
        {
            await original.OpenAsync();
        }
        var database = "ButchCrawlerResetTest_" + Guid.NewGuid().ToString("N");
        var adminString = new SqlConnectionStringBuilder(source.ConnectionString) { InitialCatalog = "master" };
        await using var admin = new SqlConnection(adminString.ConnectionString); await admin.OpenAsync();
        async Task Exec(SqlConnection connection, string sql)
        { using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 120; await command.ExecuteNonQueryAsync(); }
        async Task<long> Scalar(SqlConnection connection, string sql)
        { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync()); }
        void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
        var created = false;
        var safetyBackups = new List<string>();
        try
        {
            await Exec(admin, $"CREATE DATABASE [{database}]"); created = true;
            var target = new SqlConnectionStringBuilder(source.ConnectionString) { InitialCatalog = database };
            await using var connection = new SqlConnection(target.ConnectionString); await connection.OpenAsync();
            await Exec(connection, @"
CREATE TABLE dbo.SeedUrls(Id INT IDENTITY PRIMARY KEY,Url NVARCHAR(200) NOT NULL,IsActive BIT NOT NULL DEFAULT 1,
 LastCrawledAt DATETIME2 NULL,HealthStatus NVARCHAR(32) NULL,ConsecutiveFailures INT NOT NULL DEFAULT 0,
 FirstFailureAt DATETIME2 NULL,LastFailureAt DATETIME2 NULL,LastFailureStatus INT NULL,LastFailureReason NVARCHAR(1000) NULL,
 NextHealthCheckAt DATETIME2 NULL,DisabledReason NVARCHAR(500) NULL,AutoDisabled BIT NOT NULL DEFAULT 0);
CREATE TABLE dbo.SeedKeywords(Id INT IDENTITY PRIMARY KEY,Keyword NVARCHAR(200) NOT NULL,IsActive BIT NOT NULL DEFAULT 1);
CREATE TABLE dbo.SchemaVersion(Version INT NOT NULL);
CREATE TABLE dbo.WorkItem(Id INT IDENTITY(10,2) PRIMARY KEY,Value INT CONSTRAINT CK_Value CHECK(Value>0),KeywordId INT REFERENCES dbo.SeedKeywords(Id));
CREATE TABLE dbo.NoIdentity(Value INT NOT NULL);
CREATE TABLE dbo.CrawlQueue(Id INT IDENTITY PRIMARY KEY,Url NVARCHAR(2000),ParentUrl NVARCHAR(2000),Depth INT,Status NVARCHAR(50) DEFAULT N'pending',Attempts INT DEFAULT 0,LastError NVARCHAR(MAX),updated_at DATETIME2,locked_until DATETIME2,LockToken UNIQUEIDENTIFIER);
CREATE TABLE dbo.CrawlResults(Url NVARCHAR(2000),status_code INT,content NVARCHAR(MAX),created_at DATETIME2,updated_at DATETIME2);
CREATE TABLE dbo.VisitedUrls(url NVARCHAR(2000),content_hash NVARCHAR(100));
INSERT dbo.SeedUrls(Url,LastCrawledAt,HealthStatus,ConsecutiveFailures,FirstFailureAt,LastFailureAt,LastFailureStatus,LastFailureReason,NextHealthCheckAt,DisabledReason,AutoDisabled,IsActive)
VALUES(N'https://example.test/KeepCase',SYSUTCDATETIME(),N'failed',4,DATEADD(DAY,-4,SYSUTCDATETIME()),SYSUTCDATETIME(),503,N'test failure',DATEADD(DAY,1,SYSUTCDATETIME()),N'test automatic disable',1,0);
INSERT dbo.SeedKeywords(Keyword) VALUES(N'keep keyword'); INSERT dbo.SchemaVersion VALUES(11);
INSERT dbo.WorkItem VALUES(1,1); INSERT dbo.NoIdentity VALUES(7);");
            await Exec(connection, @"INSERT dbo.SeedKeywords(Keyword,IsActive) VALUES(N'inactive statistics fixture',0);
INSERT dbo.CrawlQueue(Url,ParentUrl,Depth) VALUES
(N'https://example.test/KeepCase',NULL,0),
(N'https://example.test/KeepCase',N'https://example.test/other',1),
(N'https://example.test/Discovered',N'https://example.test/KeepCase',1),
(N'https://example.test/Discovered',N'https://example.test/other',2),
(N'https://example.test/discovered',N'https://example.test/other',2),
(N'https://example.test/depth-only',NULL,1),
(N'https://example.test/unknown',NULL,NULL);");
            var statistics = new DatabaseStatisticsRepository(target.ConnectionString);
            var counts = await statistics.LoadAsync();
            Check(counts is { SeedKeywords: 2, SeedUrls: 1, DiscoveredUrls: 3 },
                "About totals include inactive seeds, deduplicate discovered URLs, preserve URL case and exclude seed/unknown rows");
            await Exec(connection, "DELETE dbo.SeedKeywords WHERE Keyword=N'inactive statistics fixture'; DELETE dbo.CrawlQueue");
            var audioMigration = await File.ReadAllTextAsync("Scripts/012_CreateAudioTranscripts.sql");
            await Exec(connection, audioMigration); await Exec(connection, audioMigration);
            var audioStorage = new ButchCrawler.App.SqlStorage(target.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<ButchCrawler.App.SqlStorage>.Instance);
            var audioHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
            var audio = new ButchCrawler.App.AudioTranscript("https://example.test/Interview.wav", audioHash, "gpt-transcribe", "An oral history — café keep keyword", new() { new(0, 0, "An oral history — café keep keyword") });
            await audioStorage.SaveAudioTranscriptAsync(audio, default);
            var storedAudio = await audioStorage.GetAudioTranscriptAsync(audio.Source, audio.ContentHash, audio.Model, default);
            Check(storedAudio?.Text == audio.Text && storedAudio.Segments.Count == 1, "audio migration is idempotent and saves full Unicode transcript and segments");
            var matches = await audioStorage.SearchAudioTranscriptsAsync("café", true, default);
            Check(matches.Count == 1 && matches[0].Source == audio.Source && matches[0].MatchedKeywords == "keep keyword" &&
                await audioStorage.GetAudioTranscriptTextAsync(matches[0].Id, default) == audio.Text, "audio library searches full text and returns keyword matches with exact origin link");
            Check((await audioStorage.SearchAudioTranscriptsAsync("Interview.wav", true, default)).Count == 1 &&
                (await audioStorage.SearchAudioTranscriptsAsync("' OR 1=1 --", false, default)).Count == 0 &&
                (await audioStorage.SearchAudioTranscriptsAsync("%", false, default)).Count == 0, "audio library searches origin links and treats search input literally");
            await Exec(connection, "UPDATE dbo.SeedKeywords SET IsActive=0");
            Check((await audioStorage.SearchAudioTranscriptsAsync("", true, default)).Count == 0 &&
                (await audioStorage.SearchAudioTranscriptsAsync("", false, default)).Count == 1, "audio library defaults to active keyword matches with optional all-transcript view");
            await Exec(connection, "UPDATE dbo.SeedKeywords SET IsActive=1");
            await Exec(connection, "INSERT dbo.CrawlQueue(Url,Status) VALUES(N'https://example.test/skipped.MP3?download=1',N'completed'),(N'https://example.test/blocked.mp3',N'completed'),(N'https://example.test/Interview.wav',N'completed'),(N'https://example.test/permanent.mp3',N'dead-letter'); INSERT dbo.VisitedUrls(url) VALUES(N'https://example.test/blocked.mp3')");
            Check(await audioStorage.RequeueUnprocessedAudioAsync(default) == 1 &&
                await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.CrawlQueue WHERE Status=N'pending' AND Url=N'https://example.test/skipped.MP3?download=1'") == 1,
                "enabling automatic audio recovers skipped recordings without requeueing visited, cached or dead-letter URLs");
            Check(await audioStorage.GetAudioTranscriptAsync("https://example.test/interview.wav", audio.ContentHash, audio.Model, default) == null,
                "audio source cache preserves URL path case");
            await audioStorage.SaveAudioTranscriptAsync(audio with { Text = "Updated transcript" }, default);
            Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.AudioTranscripts") == 1 &&
                (await audioStorage.GetAudioTranscriptAsync(audio.Source, audio.ContentHash, audio.Model, default))!.Text == "Updated transcript", "audio upsert avoids duplicate cached records");
            Check(await audioStorage.GetAudioTranscriptAsync(audio.Source, Convert.ToHexString(new byte[32]), audio.Model, default) == null &&
                await audioStorage.GetAudioTranscriptAsync(audio.Source, audio.ContentHash, "different-model", default) == null, "audio cache invalidates on content or model change");
            await audioStorage.SaveAudioTranscriptAsync(audio with { IsComplete = false, ChunkSeconds = 600 }, default);
            var partialAudio = await audioStorage.GetAudioTranscriptAsync(audio.Source, audio.ContentHash, audio.Model, default);
            Check(partialAudio is { IsComplete: false, ChunkSeconds: 600 }, "audio database retains partial-progress state and segment duration for resume");
            Check((await audioStorage.SearchAudioTranscriptsAsync("", false, default)).Count == 0, "audio library does not present unfinished transcripts as completed results");
            var service = new DatabaseResetService(target.ConnectionString, database);
            var preview = await service.PreviewAsync(false);
            Check(preview.Single(t => t.Property == "dbo.SeedUrls").Preserved && preview.Single(t => t.Property == "dbo.SeedKeywords").Preserved && !preview.Single(t => t.Property == "dbo.WorkItem").Preserved,
                "reset preview preserves both seeds for first action");
            await Exec(connection, "INSERT dbo.CrawlQueue(locked_until,LockToken) VALUES(DATEADD(MINUTE,1,SYSUTCDATETIME()),NEWID())");
            try { await service.ResetAsync(false); throw new Exception("Active lease accepted"); }
            catch (SqlException ex) when (ex.Number == 51011) { Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.WorkItem") == 1, "active lease blocks reset without changes"); }
            await Exec(connection, "DELETE dbo.CrawlQueue");
            var firstReset = await service.StartNewResearchProjectAsync(false); safetyBackups.Add(firstReset.BackupPath);
            var resetCounts = await statistics.LoadAsync();
            Check(resetCounts is { SeedKeywords: 1, SeedUrls: 1, DiscoveredUrls: 0 }, "About discovered total resets with crawl data while seed totals remain");
            Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.AudioTranscripts") == 0, "database reset also clears audio transcripts while preserving seed tables");
            Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.SeedUrls WHERE Id=1 AND Url=N'https://example.test/KeepCase'") == 1 &&
                await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.SeedKeywords WHERE Id=1 AND Keyword=N'keep keyword'") == 1 &&
                await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.SchemaVersion WHERE Version=11") == 1, "reset retains exact seed records and schema version");
            Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.SeedUrls WHERE LastCrawledAt IS NULL AND ConsecutiveFailures=0 AND FirstFailureAt IS NULL AND LastFailureAt IS NULL AND LastFailureStatus IS NULL AND LastFailureReason IS NULL AND NextHealthCheckAt IS NULL AND AutoDisabled=0 AND IsActive=1") == 1,
                "reset clears seed progress and failure history and reactivates only the automatically disabled seed");
            Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.WorkItem") == 0 && await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.NoIdentity") == 0, "reset clears identity and non-identity data tables");
            Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.check_constraints WHERE name='CK_Value' AND is_disabled=0 AND is_not_trusted=0") == 1, "reset restores enabled trusted check constraints");
            await Exec(connection, "INSERT dbo.WorkItem VALUES(2,1); INSERT dbo.NoIdentity VALUES(9)");
            Check(await Scalar(connection, "SELECT MIN(Id) FROM dbo.WorkItem") == 10, "cleared identity restarts at original seed and increment");
            await Exec(connection, "CREATE TRIGGER FailKeywordDelete ON dbo.SeedKeywords AFTER DELETE AS BEGIN THROW 51022, 'Trigger should be temporarily disabled', 1; END");
            var secondReset = await service.StartNewResearchProjectAsync(true); safetyBackups.Add(secondReset.BackupPath);
            Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.SeedUrls") == 1 && await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.SeedKeywords") == 0 && await Scalar(connection, "SELECT COUNT_BIG(*) FROM dbo.WorkItem") == 0,
                "second action clears keywords and crawl data but preserves Seed URLs");
            Check(await Scalar(connection, "SELECT COUNT_BIG(*) FROM sys.triggers WHERE name=N'FailKeywordDelete' AND is_disabled=0") == 1,
                "reset temporarily disables delete triggers and restores their original enabled state");
            await Exec(connection, "DROP TRIGGER dbo.FailKeywordDelete");
            await Exec(connection, "INSERT dbo.SeedKeywords(Keyword) VALUES(N'new keyword')");
            Check(await Scalar(connection, "SELECT MIN(Id) FROM dbo.SeedKeywords") == 1, "cleared keyword identity restarts at default");
            var storage = new ButchCrawler.App.SqlStorage(target.ConnectionString, Microsoft.Extensions.Logging.Abstractions.NullLogger<ButchCrawler.App.SqlStorage>.Instance);
            var token = Guid.NewGuid();
            await Exec(connection, $"INSERT dbo.CrawlQueue(Url,LockToken,locked_until) VALUES(N'https://example.test/retry','{token}',DATEADD(MINUTE,1,SYSUTCDATETIME()))");
            var queueId = await Scalar(connection, "SELECT MAX(Id) FROM dbo.CrawlQueue");
            var retryState = await storage.HandleFetchFailureAsync(queueId, token, "https://example.test/retry", 0, "Connection failed", 2, TimeSpan.FromSeconds(60), null, null, null, null);
            Check(retryState == "pending" && await Scalar(connection, "SELECT Attempts FROM dbo.CrawlQueue WHERE Status=N'pending'") == 1,
                "failure result reports actual persisted retry state for activity display");
            token = Guid.NewGuid();
            await Exec(connection, $"UPDATE dbo.CrawlQueue SET LockToken='{token}' WHERE Id={queueId}");
            var finalState = await storage.HandleFetchFailureAsync(queueId, token, "https://example.test/retry", 503, "Server error", 2, TimeSpan.FromSeconds(60), null, null, null, null);
            Check(finalState == "dead-letter" && await Scalar(connection, "SELECT Attempts FROM dbo.CrawlQueue WHERE Status=N'dead-letter'") == 2,
                "failure result reports actual persisted dead-letter state for activity display");
            Console.WriteLine("All reset integration checks passed in " + database + ". Production data was not modified.");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            if (created) await Exec(admin, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
            foreach (var backup in safetyBackups)
                if (File.Exists(backup)) File.Delete(backup);
        }
    }
}
