using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed class StarterUrlCandidate
{
    public bool Include { get; set; }
    public required string Url { get; init; }
    public DateTime? LastSuccessfulAtUtc { get; init; }
    public required string FoundIn { get; init; }
    public required string Review { get; init; }
    public bool Recommended { get; init; }
}

public sealed record StarterDatabasePreview(
    long ExistingSeedUrls,
    long SeedKeywords,
    ObservableCollection<StarterUrlCandidate> Candidates);

public sealed record StarterDatabaseBuildResult(
    string StarterBackupPath,
    string SeedExportPath,
    string SafetyBackupPath,
    string StarterDatabaseName,
    long SeedUrls,
    long SeedKeywords,
    int PromotedUrls,
    int ClearedTables);

/// <summary>
/// Produces a separate, logically clean starter database. The source database is
/// opened only for reads and a COPY_ONLY backup; destructive work is restricted
/// to the fixed starter database name.
/// </summary>
public sealed class StarterDatabaseService
{
    public const string DefaultSourceDatabase = "ButchCrawlerDB";
    public const string DefaultStarterDatabase = "HistoryCrawlerStarterDB";

    private readonly string _sourceConnectionString;
    private readonly string _sourceDatabase;
    private readonly string _starterDatabase;

    public StarterDatabaseService(
        string sourceConnectionString,
        string sourceDatabase = DefaultSourceDatabase,
        string starterDatabase = DefaultStarterDatabase)
    {
        _sourceConnectionString = sourceConnectionString;
        _sourceDatabase = sourceDatabase;
        _starterDatabase = starterDatabase;
    }

    public async Task<StarterDatabasePreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenSourceAsync(cancellationToken);
        var existingSeeds = await CountAsync(connection, "dbo.SeedUrls", cancellationToken);
        var keywords = await CountAsync(connection, "dbo.SeedKeywords", cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.CommandText = "SELECT CASE WHEN COL_LENGTH(N'dbo.CrawlArtifacts',N'RawHtmlBlobId') IS NULL THEN 0 ELSE 1 END;";
        var compressedHtml = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1
            ? " OR RawHtmlBlobId IS NOT NULL"
            : "";
        command.CommandText = $@"
WITH Successful AS
(
    SELECT COALESCE(NULLIF(LTRIM(RTRIM(FinalUrl)), N''), LTRIM(RTRIM(Url))) AS Url,
           RetrievedAt AS SuccessfulAt, N'page archive' AS FoundIn
    FROM dbo.CrawlArtifacts
    WHERE StatusCode BETWEEN 200 AND 299
      AND (NULLIF(LTRIM(RTRIM(RawHtml)), N'') IS NOT NULL OR NULLIF(LTRIM(RTRIM(Title)), N'') IS NOT NULL{compressedHtml})
    UNION ALL
    SELECT LTRIM(RTRIM(Url)), COALESCE(updated_at, created_at), N'crawl result'
    FROM dbo.CrawlResults
    WHERE status_code BETWEEN 200 AND 299 AND NULLIF(LTRIM(RTRIM(content)), N'') IS NOT NULL
), Candidates AS
(
    SELECT Url, MAX(SuccessfulAt) AS SuccessfulAt,
           CASE WHEN COUNT(DISTINCT FoundIn) > 1 THEN N'page archive + crawl result' ELSE MAX(FoundIn) END AS FoundIn
    FROM Successful
    WHERE Url LIKE N'http://%' OR Url LIKE N'https://%'
    GROUP BY Url
)
SELECT c.Url, c.SuccessfulAt, c.FoundIn
FROM Candidates c
WHERE LEN(c.Url) <= 2000
  AND CHARINDEX(N'#', c.Url) = 0
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.SeedUrls s
      WHERE s.UrlHash = HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), c.Url))
        AND s.Url COLLATE Latin1_General_100_BIN2 = c.Url COLLATE Latin1_General_100_BIN2
  )
ORDER BY c.Url;";

        var candidates = new ObservableCollection<StarterUrlCandidate>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var url = reader.GetString(0);
            var assessment = Assess(url);
            candidates.Add(new StarterUrlCandidate
            {
                Include = assessment.Recommended,
                Url = url,
                LastSuccessfulAtUtc = reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                FoundIn = reader.GetString(2),
                Review = assessment.Reason,
                Recommended = assessment.Recommended
            });
        }

        return new StarterDatabasePreview(existingSeeds, keywords, candidates);
    }

    public async Task<StarterDatabaseBuildResult> BuildAsync(
        IReadOnlyCollection<string> selectedUrls,
        string outputDirectory,
        bool replaceExistingStarter,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(_sourceDatabase, _starterDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The starter database must have a different name from the working database.");

        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var working = Path.Combine(output, "working-files");
        Directory.CreateDirectory(working);
        var backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HistoryCrawler", "Backups");
        Directory.CreateDirectory(backupDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safetyBackup = Path.Combine(backupDirectory, $"{_sourceDatabase}-before-starter-{stamp}.bak");
        var cleanBackup = Path.Combine(output, "HistoryCrawlerStarter.bak");
        var seedExport = Path.Combine(output, "HistoryCrawlerStarterSeeds.json");

        progress?.Report("Checking the working database…");
        List<DatabaseFile> sourceFiles;
        await using (var source = await OpenSourceAsync(cancellationToken))
        {
            sourceFiles = await ReadDatabaseFilesAsync(source, cancellationToken);
            progress?.Report("Creating a private safety backup. Your working database is not changed…");
            await BackupAsync(source, _sourceDatabase, safetyBackup, cancellationToken);
        }

        progress?.Report("Creating the separate starter database…");
        var masterConnectionString = BuildMasterConnectionString();
        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync(cancellationToken);
            await PrepareTargetDatabaseAsync(master, replaceExistingStarter, cancellationToken);
            await RestoreStarterAsync(master, safetyBackup, sourceFiles, working, cancellationToken);
        }

        var targetConnectionString = BuildTargetConnectionString();
        var promoted = 0;
        var clearedTables = 0;
        long seedCount;
        long keywordCount;
        try
        {
            await using var target = new SqlConnection(targetConnectionString);
            await target.OpenAsync(cancellationToken);
            await ValidateTargetAsync(target, cancellationToken);

            progress?.Report("Adding the approved successful URLs to the starter seed list…");
            promoted = await PromoteAsync(target, selectedUrls, cancellationToken);

            progress?.Report("Removing crawl results and other research data from the starter copy…");
            clearedTables = await ClearNonSeedDataAsync(target, cancellationToken);

            progress?.Report("Resetting seed progress for a new installation…");
            await ResetSeedStateAsync(target, cancellationToken);

            progress?.Report("Verifying that only seeds and the schema version contain records…");
            await VerifyCleanAsync(target, cancellationToken);
            seedCount = await CountAsync(target, "dbo.SeedUrls", cancellationToken);
            keywordCount = await CountAsync(target, "dbo.SeedKeywords", cancellationToken);

            progress?.Report("Writing a portable seed-list export…");
            await ExportSeedsAsync(target, seedExport, cancellationToken);

            progress?.Report("Creating and checking the clean starter backup…");
            await BackupAsync(target, _starterDatabase, cleanBackup, cancellationToken);
        }
        catch
        {
            // Keep the private safety backup. It is the recovery point if SQL Server
            // or the computer stopped during preparation.
            throw;
        }

        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync(cancellationToken);
            using var verify = master.CreateCommand();
            verify.CommandTimeout = 600;
            verify.CommandText = $"RESTORE VERIFYONLY FROM DISK = N'{SqlLiteral(cleanBackup)}' WITH CHECKSUM;";
            await verify.ExecuteNonQueryAsync(cancellationToken);
        }

        progress?.Report("Starter database ready. The working database was not changed.");
        return new StarterDatabaseBuildResult(cleanBackup, seedExport, safetyBackup, _starterDatabase,
            seedCount, keywordCount, promoted, clearedTables);
    }

    public static (bool Recommended, string Reason) Assess(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (false, "Invalid or unsupported web address");

        var value = (uri.AbsolutePath + uri.Query).ToLowerInvariant();
        string[] privateWords = ["login", "logout", "sign-in", "signin", "account", "session", "token", "captcha", "checkout", "cart"];
        if (privateWords.Any(value.Contains)) return (false, "Review: login, account, or session-like address");

        string[] generatedWords = ["/search", "?search", "?q=", "calendar", "sort=", "page=", "offset=", "cursor="];
        if (generatedWords.Any(value.Contains)) return (false, "Review: search, calendar, sorting, or paging address");
        if (!string.IsNullOrEmpty(uri.Query)) return (false, "Review: address contains query values");
        return (true, "Recommended: successful address without query values");
    }

    private async Task<SqlConnection> OpenSourceAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_sourceConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            if (!string.Equals(connection.Database, _sourceDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Starter preparation is restricted to the {_sourceDatabase} working database.");
            using var check = connection.CreateCommand();
            check.CommandText = @"SELECT CASE WHEN OBJECT_ID(N'dbo.SeedUrls',N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.SeedKeywords',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SchemaVersion',N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.CrawlArtifacts',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.CrawlResults',N'U') IS NOT NULL
 THEN 1 ELSE 0 END";
            if (Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidOperationException("The required seed, schema, and crawl-result tables were not found.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private string BuildMasterConnectionString()
    {
        var builder = new SqlConnectionStringBuilder(_sourceConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private string BuildTargetConnectionString()
    {
        var builder = new SqlConnectionStringBuilder(_sourceConnectionString)
        {
            InitialCatalog = _starterDatabase,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private async Task PrepareTargetDatabaseAsync(SqlConnection master, bool replace, CancellationToken cancellationToken)
    {
        using var exists = master.CreateCommand();
        exists.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = _starterDatabase;
        exists.CommandText = "SELECT CASE WHEN DB_ID(@name) IS NULL THEN 0 ELSE 1 END";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return;
        if (!replace)
            throw new InvalidOperationException($"The {_starterDatabase} database already exists. Select Replace only after reviewing this warning.");

        using var drop = master.CreateCommand();
        drop.CommandTimeout = 180;
        drop.CommandText = $"ALTER DATABASE {QuoteIdentifier(_starterDatabase)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {QuoteIdentifier(_starterDatabase)};";
        await drop.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RestoreStarterAsync(SqlConnection master, string backup, IReadOnlyList<DatabaseFile> files,
        string workingDirectory, CancellationToken cancellationToken)
    {
        var clauses = new List<string>();
        var dataNumber = 0;
        var logNumber = 0;
        foreach (var file in files)
        {
            string destination;
            if (file.Type == "L")
            {
                logNumber++;
                destination = Path.Combine(workingDirectory, logNumber == 1 ? "HistoryCrawlerStarter.ldf" : $"HistoryCrawlerStarter_log{logNumber}.ldf");
            }
            else
            {
                dataNumber++;
                destination = Path.Combine(workingDirectory, dataNumber == 1 ? "HistoryCrawlerStarter.mdf" : $"HistoryCrawlerStarter_data{dataNumber}.ndf");
            }
            clauses.Add($"MOVE N'{SqlLiteral(file.LogicalName)}' TO N'{SqlLiteral(destination)}'");
        }
        if (dataNumber == 0 || logNumber == 0) throw new InvalidOperationException("SQL Server did not report both a data file and a log file.");

        using var restore = master.CreateCommand();
        restore.CommandTimeout = 1200;
        restore.CommandText = $"RESTORE DATABASE {QuoteIdentifier(_starterDatabase)} FROM DISK=N'{SqlLiteral(backup)}' WITH {string.Join(", ", clauses)}, REPLACE, RECOVERY, STATS=10;";
        await restore.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<DatabaseFile>> ReadDatabaseFilesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, type FROM sys.database_files ORDER BY file_id";
        var result = new List<DatabaseFile>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new DatabaseFile(reader.GetString(0), reader.GetByte(1) == 1 ? "L" : "D"));
        return result;
    }

    private static async Task BackupAsync(SqlConnection connection, string database, string path, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1200;
        command.CommandText = $"BACKUP DATABASE {QuoteIdentifier(database)} TO DISK=N'{SqlLiteral(path)}' WITH COPY_ONLY, INIT, CHECKSUM, COMPRESSION, STATS=10;";
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqlException ex) when (ex.Message.Contains("COMPRESSION", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = $"BACKUP DATABASE {QuoteIdentifier(database)} TO DISK=N'{SqlLiteral(path)}' WITH COPY_ONLY, INIT, CHECKSUM, STATS=10;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ValidateTargetAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!string.Equals(connection.Database, _starterDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to prepare a database other than the fixed starter database.");
        using var command = connection.CreateCommand();
        command.CommandText = @"SELECT CASE WHEN OBJECT_ID(N'dbo.SeedUrls',N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.SeedKeywords',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SchemaVersion',N'U') IS NOT NULL THEN 1 ELSE 0 END";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new InvalidOperationException("The restored starter copy does not contain the required tables.");
    }

    private static async Task<int> PromoteAsync(SqlConnection connection, IReadOnlyCollection<string> urls, CancellationToken cancellationToken)
    {
        var approved = urls.Select(value => value.Trim())
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && value.Length <= 2000)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (approved.Length == 0) return 0;

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var added = 0;
        try
        {
            foreach (var url in approved)
            {
                var uri = new Uri(url);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.SeedUrls WITH (UPDLOCK, SERIALIZABLE)
               WHERE Url COLLATE Latin1_General_100_BIN2=@url COLLATE Latin1_General_100_BIN2)
BEGIN
 INSERT dbo.SeedUrls(Url,Domain,Category,Priority,MaxDepth,FollowExternal,IsActive,Notes,CreatedAt,LastCrawledAt,SourceType,DiscoverySource,Tags)
 VALUES(@url,@domain,N'Promoted successful URL',-10,0,0,1,N'Promoted while preparing a clean starter database.',SYSUTCDATETIME(),NULL,N'promoted-success',N'successful crawl',N'starter-promoted');
 SELECT 1;
END
ELSE SELECT 0;";
                command.Parameters.Add("@url", SqlDbType.NVarChar, 2000).Value = url;
                command.Parameters.Add("@domain", SqlDbType.NVarChar, 256).Value = uri.Host;
                added += Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            }
            await transaction.CommitAsync(cancellationToken);
            return added;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<int> ClearNonSeedDataAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 1200;
        command.CommandText = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 DECLARE @lockResult INT;
 EXEC @lockResult=sys.sp_getapplock @Resource=N'HistoryCrawler.StarterPreparation',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=0;
 IF @lockResult<0 THROW 51100,'Another starter preparation is running.',1;

 DECLARE @allTables TABLE(Qualified nvarchar(517));
 INSERT @allTables
 SELECT QUOTENAME(s.name)+N'.'+QUOTENAME(t.name)
 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
 WHERE t.is_ms_shipped=0;

 DECLARE @tables TABLE(Qualified nvarchar(517), HasIdentity bit);
 INSERT @tables
 SELECT QUOTENAME(s.name)+N'.'+QUOTENAME(t.name), CASE WHEN EXISTS(SELECT 1 FROM sys.identity_columns i WHERE i.object_id=t.object_id) THEN 1 ELSE 0 END
 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
 WHERE NOT(s.name=N'dbo' AND t.name IN(N'SeedUrls',N'SeedKeywords',N'SchemaVersion'))
   AND t.is_ms_shipped=0;

 DECLARE @table nvarchar(517),@identity bit,@sql nvarchar(max),@count int=0;
 -- Constraints on the preserved seed tables are disabled too. This allows a
 -- referencing non-seed table to be cleared without changing or deleting a seed.
 DECLARE disable_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified FROM @allTables;
 OPEN disable_cursor; FETCH NEXT FROM disable_cursor INTO @table;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'ALTER TABLE '+@table+N' NOCHECK CONSTRAINT ALL; DISABLE TRIGGER ALL ON '+@table+N';';
  EXEC sys.sp_executesql @sql;
  FETCH NEXT FROM disable_cursor INTO @table;
 END
 CLOSE disable_cursor; DEALLOCATE disable_cursor;

 DECLARE table_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified,HasIdentity FROM @tables;
 OPEN table_cursor; FETCH NEXT FROM table_cursor INTO @table,@identity;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'DELETE FROM '+@table+N';';
  IF @identity=1 SET @sql+=N'DBCC CHECKIDENT('''+REPLACE(@table,'''','''''')+N''',RESEED,0) WITH NO_INFOMSGS;';
  EXEC sys.sp_executesql @sql;
  SET @count+=1;
  FETCH NEXT FROM table_cursor INTO @table,@identity;
 END
 CLOSE table_cursor; DEALLOCATE table_cursor;

 DECLARE restore_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Qualified FROM @allTables;
 OPEN restore_cursor; FETCH NEXT FROM restore_cursor INTO @table;
 WHILE @@FETCH_STATUS=0
 BEGIN
  SET @sql=N'ENABLE TRIGGER ALL ON '+@table+N'; ALTER TABLE '+@table+N' WITH CHECK CHECK CONSTRAINT ALL;';
  EXEC sys.sp_executesql @sql;
  FETCH NEXT FROM restore_cursor INTO @table;
 END
 CLOSE restore_cursor; DEALLOCATE restore_cursor;
 COMMIT;
 SELECT @count;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ResetSeedStateAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.CommandText = @"
UPDATE dbo.SeedUrls SET LastCrawledAt=NULL;
IF COL_LENGTH(N'dbo.SeedUrls',N'HealthStatus') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET HealthStatus=N''healthy''');
IF COL_LENGTH(N'dbo.SeedUrls',N'ConsecutiveFailures') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET ConsecutiveFailures=0');
IF COL_LENGTH(N'dbo.SeedUrls',N'FirstFailureAt') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET FirstFailureAt=NULL');
IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureAt') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET LastFailureAt=NULL');
IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureStatus') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET LastFailureStatus=NULL');
IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureReason') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET LastFailureReason=NULL');
IF COL_LENGTH(N'dbo.SeedUrls',N'NextHealthCheckAt') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET NextHealthCheckAt=NULL');
IF COL_LENGTH(N'dbo.SeedUrls',N'AutoDisabled') IS NOT NULL EXEC(N'UPDATE dbo.SeedUrls SET AutoDisabled=0');";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task VerifyCleanAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 180;
        command.CommandText = @"
DECLARE @table nvarchar(517),@sql nvarchar(max),@rows bigint;
DECLARE table_cursor CURSOR LOCAL FAST_FORWARD FOR
SELECT QUOTENAME(s.name)+N'.'+QUOTENAME(t.name)
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE NOT(s.name=N'dbo' AND t.name IN(N'SeedUrls',N'SeedKeywords',N'SchemaVersion')) AND t.is_ms_shipped=0;
OPEN table_cursor; FETCH NEXT FROM table_cursor INTO @table;
WHILE @@FETCH_STATUS=0
BEGIN
 SET @rows=0; SET @sql=N'SELECT @value=COUNT_BIG(*) FROM '+@table;
 EXEC sys.sp_executesql @sql,N'@value bigint OUTPUT',@rows OUTPUT;
 IF @rows<>0 BEGIN CLOSE table_cursor; DEALLOCATE table_cursor; THROW 51101,'A non-seed table still contains records.',1; END;
 FETCH NEXT FROM table_cursor INTO @table;
END
CLOSE table_cursor; DEALLOCATE table_cursor;
IF EXISTS(SELECT 1 FROM dbo.SeedUrls WHERE Url IS NULL OR LTRIM(RTRIM(Url))=N'') THROW 51102,'A blank seed URL remains.',1;
SELECT 1;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new InvalidOperationException("The starter database verification did not complete.");
    }

    private static async Task ExportSeedsAsync(SqlConnection connection, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("format", "HistoryCrawler starter seeds");
        writer.WriteString("generatedAtUtc", DateTime.UtcNow);
        writer.WriteStartArray("seedKeywords");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Keyword,IsActive,Source FROM dbo.SeedKeywords ORDER BY Keyword";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                writer.WriteStartObject();
                writer.WriteString("keyword", reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                writer.WriteBoolean("active", reader.IsDBNull(1) || reader.GetBoolean(1));
                if (!reader.IsDBNull(2)) writer.WriteString("source", reader.GetString(2));
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("seedUrls");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"SELECT Url,Domain,Category,Priority,MaxDepth,FollowExternal,IsActive,SourceType,DiscoverySource,Tags
FROM dbo.SeedUrls ORDER BY Id";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                writer.WriteStartObject();
                writer.WriteString("url", reader.GetString(0));
                if (!reader.IsDBNull(1)) writer.WriteString("domain", reader.GetString(1));
                if (!reader.IsDBNull(2)) writer.WriteString("category", reader.GetString(2));
                if (!reader.IsDBNull(3)) writer.WriteNumber("priority", reader.GetInt32(3));
                if (!reader.IsDBNull(4)) writer.WriteNumber("maxDepth", reader.GetInt32(4));
                writer.WriteBoolean("followExternal", !reader.IsDBNull(5) && reader.GetBoolean(5));
                writer.WriteBoolean("active", reader.IsDBNull(6) || reader.GetBoolean(6));
                if (!reader.IsDBNull(7)) writer.WriteString("sourceType", reader.GetString(7));
                if (!reader.IsDBNull(8)) writer.WriteString("discoverySource", reader.GetString(8));
                if (!reader.IsDBNull(9)) writer.WriteString("tags", reader.GetString(9));
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<long> CountAsync(SqlConnection connection, string qualifiedTable, CancellationToken cancellationToken)
    {
        if (qualifiedTable is not ("dbo.SeedUrls" or "dbo.SeedKeywords"))
            throw new ArgumentOutOfRangeException(nameof(qualifiedTable));
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {qualifiedTable}";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string QuoteIdentifier(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private sealed record DatabaseFile(string LogicalName, string Type);
}
