using System.Data;
using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ButchCrawler.App;
using ButchCrawler.Operator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

if (args.FirstOrDefault() == "--exit-ui") { ExitUiTests.Run(); return; }
if (args.FirstOrDefault() == "--key-settings")
{
    var count = 0;
    KeySettingsTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} OpenAI key and optional-feature checks passed.");
    return;
}
if (args.FirstOrDefault() == "--ai-tests")
{
    var count = 0;
    await AiTests.RunAsync((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} optional-AI service checks passed.");
    return;
}
if (args.FirstOrDefault() == "--seed-health-tests")
{
    var count = 0;
    SeedHealthTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} seed health checks passed.");
    return;
}
if (args.FirstOrDefault() == "--security-tests")
{
    var count = 0;
    SecurityHardeningTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} security hardening checks passed.");
    return;
}
if (args.FirstOrDefault() == "--coverage-tests")
{
    var count = 0;
    await CrawlCoverageTests.RunAsync((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} crawl coverage checks passed.");
    return;
}
if (args.FirstOrDefault() == "--structured-data-tests")
{
    var count = 0;
    await StructuredDataTests.RunAsync((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} structured-data checks passed.");
    return;
}
if (args.FirstOrDefault() == "--media-probe") { await MediaProbe.RunAsync(args[1]); return; }
if (args.FirstOrDefault() == "--collection-search-tests")
{
    var count = 0;
    CollectionSearchQueryTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} collection search checks passed.");
    return;
}
if (args.FirstOrDefault() == "--activity-tests")
{
    var count = 0;
    ActivityTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} dashboard and activity checks passed.");
    return;
}
if (args.FirstOrDefault() == "--historical-newspaper-tests")
{
    var count = 0;
    HistoricalNewspaperTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} historical newspaper checks passed.");
    return;
}
if (args.FirstOrDefault() == "--starter-tests")
{
    var count = 0;
    StarterDatabaseTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} starter-database checks passed.");
    return;
}
if (args.FirstOrDefault() == "--database-reset-tests")
{
    var count = 0;
    DatabaseResetTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} new-project reset checks passed.");
    return;
}
if (args.FirstOrDefault() == "--reset-dialog-tests")
{
    var count = 0;
    ResetDialogTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} new-project dialog checks passed.");
    return;
}
if (args.FirstOrDefault() == "--community-response-tests")
{
    var count = 0;
    CommunityResponseTests.Run((condition, name) => { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; });
    Console.WriteLine($"All {count} community-response checks passed.");
    return;
}
if (args.FirstOrDefault() == "--community-schema")
{
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
    var settings = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App"))
        .AddJsonFile("appsettings.json").AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddEnvironmentVariables().Build();
    var repository = new CommunityResponseRepository(settings.GetConnectionString("DefaultConnection")!);
    await repository.InitializeAsync();
    var saved = await repository.LoadAsync();
    Console.WriteLine($"Community response schema verified: {saved.Counts.Subscriptions:N0} subscription(s), {saved.Counts.Suggestions:N0} suggestion(s).");
    return;
}
if (args.FirstOrDefault() == "--sql") { await SqlIntegration.RunAsync(args[1]); return; }
if (args.FirstOrDefault() == "--reset-sql") { await ResetIntegrationTests.RunAsync(); return; }
if (args.FirstOrDefault() == "--starter-preview")
{
    var config = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App"))
        .AddJsonFile("appsettings.json").AddEnvironmentVariables().Build();
    var service = new StarterDatabaseService(config.GetConnectionString("DefaultConnection")!);
    var preview = await service.PreviewAsync();
    Console.WriteLine($"Existing seeds: {preview.ExistingSeedUrls:N0}");
    Console.WriteLine($"Seed keywords: {preview.SeedKeywords:N0}");
    Console.WriteLine($"Successful URLs available: {preview.Candidates.Count:N0}");
    Console.WriteLine($"Recommended: {preview.Candidates.Count(candidate => candidate.Recommended):N0}");
    return;
}
if (args.FirstOrDefault() == "--ai-sql") { await AiTests.RunSqlAsync(); return; }
if (args.FirstOrDefault() == "--crawl-sql") { await CrawlSqlTests.RunAsync(); return; }
if (args.FirstOrDefault() is "--install-ai-schema" or "--ai-status")
{
    var config = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App")).AddJsonFile("appsettings.json")
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true).AddEnvironmentVariables().Build();
    var repository = new SqlAiRepository(config.GetConnectionString("DefaultConnection")!);
    if (args[0] == "--ai-status")
    {
        Console.WriteLine($"AI database verified: {(await repository.SearchAsync("", default)).Count} browsable sources (up to 100); {(await repository.HistoryAsync(default)).Count} saved results (up to 200). No API request sent."); return;
    }
    await repository.InitializeAsync(default);
    Console.WriteLine("AI schema installed. Existing records preserved."); return;
}
if (args.FirstOrDefault() == "--preview") { UiPreview.Run(args[1]); return; }
if (args.FirstOrDefault() is "--keyword-status" or "--local-status")
{
    var settings = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App")).AddJsonFile("appsettings.json")
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json",optional:true).AddEnvironmentVariables().Build();
    await using var connection = new Microsoft.Data.SqlClient.SqlConnection(settings.GetConnectionString("DefaultConnection")); await connection.OpenAsync();
    using var command=connection.CreateCommand(); command.CommandTimeout=15;
    if (args[0] == "--local-status")
    {
        command.CommandText = @"SELECT DB_NAME(), @@SERVERNAME;
SELECT COUNT_BIG(*) FROM dbo.CrawlResults WHERE Url LIKE '[A-Za-z]:\%' OR Url LIKE 'file:%' OR Url LIKE '\\%';
SELECT TOP(10) Url,LEN(content),updated_at FROM dbo.CrawlResults WHERE Url LIKE '[A-Za-z]:\%' OR Url LIKE 'file:%' OR Url LIKE '\\%' ORDER BY updated_at DESC;";
        using var localReader = await command.ExecuteReaderAsync();
        do { while (await localReader.ReadAsync()) Console.WriteLine(string.Join(" | ", Enumerable.Range(0, localReader.FieldCount).Select(i => localReader.GetValue(i).ToString()))); }
        while (await localReader.NextResultAsync());
        return;
    }
    command.CommandText="SELECT IsActive,COUNT_BIG(*) FROM dbo.SeedKeywords GROUP BY IsActive; SELECT TOP(10) Keyword FROM dbo.SeedKeywords WHERE IsActive=1 ORDER BY Id; SELECT COUNT_BIG(*),COALESCE(SUM(CONVERT(bigint,MatchCount)),0) FROM dbo.CrawlKeywordHits;";
    using var reader=await command.ExecuteReaderAsync();
    while(await reader.ReadAsync()) Console.WriteLine($"Keywords active={reader[0]}: {reader[1]}");
    await reader.NextResultAsync(); while(await reader.ReadAsync()) Console.WriteLine("Keyword sample: "+reader.GetString(0));
    await reader.NextResultAsync(); if(await reader.ReadAsync()) Console.WriteLine($"Stored keyword-hit rows: {reader[0]}; total occurrences: {reader[1]}");
    return;
}
if (args.FirstOrDefault() is "--check-sql" or "--inspect-sql" or "--install-audio-schema" or "--about-stats")
{
    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
    var settings = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App"))
        .AddJsonFile("appsettings.json").AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddEnvironmentVariables().Build();
    var connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(settings.GetConnectionString("DefaultConnection")) { ConnectTimeout = 5 };
    try
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString.ConnectionString);
        await connection.OpenAsync();
        if (args[0] == "--about-stats")
        {
            var counts = await new DatabaseStatisticsRepository(connectionString.ConnectionString).LoadAsync();
            Console.WriteLine($"Seed Keywords: {counts.SeedKeywords:N0}; Seed URLs: {counts.SeedUrls:N0}; distinct discovered non-seed URLs: {counts.DiscoveredUrls:N0}");
            return;
        }
        if (args[0] == "--install-audio-schema")
        {
            if (connection.Database != "ButchCrawlerDB") throw new InvalidOperationException("Audio schema install is restricted to ButchCrawlerDB.");
            using var install = connection.CreateCommand();
            install.CommandText = await File.ReadAllTextAsync("Scripts/012_CreateAudioTranscripts.sql");
            await install.ExecuteNonQueryAsync();
            Console.WriteLine("AudioTranscripts schema is installed. Existing data was not cleared.");
            return;
        }
        if (args[0] == "--inspect-sql")
        {
            using var inspect = connection.CreateCommand();
            inspect.CommandText = @"SELECT s.name,p.name,OBJECT_DEFINITION(p.object_id) FROM sys.procedures p JOIN sys.schemas s ON s.schema_id=p.schema_id WHERE p.name LIKE '%WideData%' OR p.name LIKE '%WipeData%';
SELECT s.name,t.name,c.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id LEFT JOIN sys.identity_columns c ON c.object_id=t.object_id ORDER BY s.name,t.name;
SELECT OBJECT_SCHEMA_NAME(parent_object_id),OBJECT_NAME(parent_object_id),OBJECT_NAME(referenced_object_id) FROM sys.foreign_keys;";
            using var reader = await inspect.ExecuteReaderAsync();
            do { while (await reader.ReadAsync()) Console.WriteLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString()))); }
            while (await reader.NextResultAsync());
            return;
        }
        using var command = connection.CreateCommand(); command.CommandText = "SELECT 1"; command.CommandTimeout = 5;
        Console.WriteLine("SQL connection verified: " + (Convert.ToInt32(await command.ExecuteScalarAsync()) == 1));
    }
    catch (Exception ex) { Console.WriteLine("SQL connection check failed: " + ex.Message); Environment.ExitCode = 1; }
    return;
}

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
var normalizeMethod = typeof(Fetcher).Assembly.GetType("ButchCrawler.App.UrlHelper")!.GetMethod("NormalizeUrl")!;
string? Normalize(string url) => (string?)normalizeMethod.Invoke(null, new object[] { url });
Check(Normalize("HTTPS://EXAMPLE.COM:443/Path/?q=A#frag") == "https://example.com/Path/?q=A", "normalize host and fragment, preserve path/query/trailing slash");
Check(Normalize("https://example.com/Path") != Normalize("https://example.com/path"), "case-sensitive path identity");
Check(Normalize("https://example.com/path/") != Normalize("https://example.com/path"), "trailing slash identity");
Check(Normalize("https://example.com/?id=A") != Normalize("https://example.com/?id=a"), "query case identity");

var limiter = new HostConcurrencyLimiter(1, TimeSpan.FromMilliseconds(150));
using (await limiter.WaitAsync("test", CancellationToken.None)) { }
using (var cancel = new CancellationTokenSource(20))
{
    try { using var slot = await limiter.WaitAsync("test", cancel.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
}
using (var deadline = new CancellationTokenSource(1000))
using (await limiter.WaitAsync("test", deadline.Token)) { Check(true, "cancellation returns acquired host slot"); }
var parallelLimiter = new HostConcurrencyLimiter(4, TimeSpan.FromMilliseconds(70));
var clock = Stopwatch.StartNew();
var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
{
    using var slot = await parallelLimiter.WaitAsync("shared", CancellationToken.None);
    return clock.Elapsed.TotalMilliseconds;
}));
Array.Sort(starts);
Check(starts.Zip(starts.Skip(1), (a, b) => b - a).All(gap => gap >= 50), "parallel host starts stay spaced");

using var stopServer = new CancellationTokenSource();
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
var server = Task.Run(async () =>
{
    while (!stopServer.IsCancellationRequested)
    {
        TcpClient client;
        try { client = await listener.AcceptTcpClientAsync(stopServer.Token); }
        catch (OperationCanceledException) { break; }
        _ = Task.Run(async () =>
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var first = await reader.ReadLineAsync(stopServer.Token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stopServer.Token))) { }
                var path = first.Split(' ')[1];
                if (path.StartsWith("/status/"))
                {
                    var code = path.Split('/')[2];
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Fixture\r\nRetry-After: 180\r\nContent-Type: text/html\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); return;
                }
                if (path is "/challenge-header" or "/forbidden-challenge")
                {
                    var code=path=="/forbidden-challenge" ? 403 : 200;
                    const string page="<html><title>Just a moment...</title><script src='/cdn-cgi/challenge-platform/test'></script></html>";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Fixture\r\ncf-mitigated: challenge\r\nContent-Type: text/html\r\nContent-Length: {page.Length}\r\nConnection: close\r\n\r\n{page}")); return;
                }
                if (path == "/redirect404" || path == "/redirectloop" || path == "/redirectslash")
                {
                    var destination = path == "/redirect404" ? "/status/404" : path == "/redirectloop" ? "/redirectloop" : "/article/";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 301 Moved\r\nLocation: {destination}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")); return;
                }
                if (path == "/redirect")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: /article\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    return;
                }
                var body = path.StartsWith("/large") ? new string('x', 257)
                    : path == "/challenge" ? "<html>Please verify you are a human <div class='cf-turnstile'></div></html>"
                    : path == "/embedded" ? "<html><title>Archives</title>Historical collection<script src='/cdn-cgi/challenge-platform/widget.js'>cf-chl-widget human verification</script></html>"
                    : "<html><title>Article</title>Cloudflare recaptcha turnstile explained<script src='https://example.com/recaptcha.js'></script></html>";
                if (path == "/stall")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 100\r\nConnection: close\r\n\r\nx"));
                    await Task.Delay(4000, stopServer.Token); return;
                }
                var binary = path == "/large.pdf";
                var header = $"HTTP/1.1 200 OK\r\nContent-Type: {(binary ? "application/pdf" : "text/html")}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n";
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header + bytes.Length.ToString("X") + "\r\n"));
                await stream.WriteAsync(bytes);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        });
    }
});
try
{
    using var http = new HttpClient();
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Crawler:RequestTimeoutSeconds"] = "1", ["Crawler:MaxContentBytes"] = "256",
        ["Crawler:MaxAttempts"] = "1", ["Crawler:MinDelayMs"] = "0",
        ["Crawler:AllowPrivateNetwork"] = "true"
    }).Build();
    var fetcher = new Fetcher(http, NullLogger<Fetcher>.Instance, config, new NoopCaptchaSolver());
    var redirectChecks = 0;
    var rejectedRedirect = await fetcher.FetchAsync(address + "/redirect", CancellationToken.None, (target, _) =>
    {
        redirectChecks++;
        Check(target.AbsolutePath == "/article", "redirect policy receives resolved target");
        return Task.FromResult(false);
    });
    Check(!rejectedRedirect.Success && rejectedRedirect.StatusCode == 302 && rejectedRedirect.Retryable == false && redirectChecks == 1,
        "redirect policy rejects destination before fetch");
    var followedRedirect = await fetcher.FetchAsync(address + "/redirect", CancellationToken.None, (_, _) => Task.FromResult(true));
    Check(followedRedirect.Success && followedRedirect.StatusCode == 200 && followedRedirect.FinalUrl == address + "/article", "permitted redirect reports final HTTP status");
    var missingRedirect = await fetcher.FetchAsync(address + "/redirect404", default, (_,_)=>Task.FromResult(true));
    Check(!missingRedirect.Success && missingRedirect.StatusCode == 404 && missingRedirect.Retryable == false, "redirected 404 is a permanent failure rather than completed content");
    var slashRedirect = await fetcher.FetchAsync(address + "/redirectslash", default, (_,_)=>Task.FromResult(true));
    Check(slashRedirect.Success && slashRedirect.FinalUrl.EndsWith("/article/"), "trailing-slash redirect retrieves target content");
    var loop = await fetcher.FetchAsync(address + "/redirectloop", default, (_,_)=>Task.FromResult(true));
    Check(!loop.Success && loop.Retryable == false && loop.Error.Contains("redirect limit"), "redirect loops fail without processing redirect body as content");
    var guardedConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Crawler:MaxAttempts"] = "1", ["Crawler:MinDelayMs"] = "0"
    }).Build();
    using var guardedHttp = new HttpClient();
    var guardedFetcher = new Fetcher(guardedHttp, NullLogger<Fetcher>.Instance, guardedConfig, new NoopCaptchaSolver());
    Check(!(await guardedFetcher.FetchAsync(address, CancellationToken.None)).Success, "default fetch rejects loopback network");
    foreach (var headers in new[] { false, true })
    {
        Task<FetchResult> Fetch(string path, CancellationToken ct = default) => headers
            ? fetcher.FetchWithHeadersAsync(address + path, null, ct) : fetcher.FetchAsync(address + path, ct);
        foreach (var path in new[] { "/large", "/large.pdf" })
        {
            var result = await Fetch(path);
            Check(!result.Success && result.Error.Contains("exceeds max") && result.BodyBytes == null,
                $"reject oversized chunked {path}, headers={headers}");
        }
        var article = await Fetch("/article");
        Check(article.Success && !article.IsChallenge, $"vendor mentions/widget script are not a challenge, headers={headers}");
        var embedded = await Fetch("/embedded");
        Check(embedded.Success && !embedded.IsChallenge, $"embedded Cloudflare script does not block historical content, headers={headers}");
        foreach (var code in new[] { 403, 404, 410, 429, 503 })
        {
            var failed = await Fetch("/status/" + code);
            Check(!failed.Success && failed.StatusCode == code && failed.Retryable == (code is 429 or 503), $"HTTP {code} correctly fails and classifies queue retry, headers={headers}");
            if (code == 429) Check(failed.RetryAfter == TimeSpan.FromSeconds(180), $"HTTP 429 preserves Retry-After for scheduled queue retry, headers={headers}");
        }
        foreach(var path in new[]{"/challenge-header","/forbidden-challenge"})
        {
            var blocked=await Fetch(path);
            Check(!blocked.Success && blocked.IsChallenge,$"real verification header is recognized at {path}, headers={headers}");
        }
        var challenge = await Fetch("/challenge");
        Check(!challenge.Success && challenge.IsChallenge, $"blocking challenge detected, headers={headers}");
        var elapsed = Stopwatch.StartNew();
        var stalled = await Fetch("/stall");
        Check(!stalled.Success && elapsed.Elapsed < TimeSpan.FromSeconds(3), $"body read obeys request timeout, headers={headers}");
        using var cancel = new CancellationTokenSource(100);
        try { await Fetch("/stall", cancel.Token); throw new Exception("Cancellation swallowed"); }
        catch (OperationCanceledException) { Check(true, $"caller cancellation propagates, headers={headers}"); }
    }
}
finally { stopServer.Cancel(); listener.Stop(); await server; }

var validate = typeof(SeedEditorWindow).GetMethod("ValidateRows", BindingFlags.Static | BindingFlags.NonPublic)!;
DataTable Rows(bool keywords, params string[] values)
{
    var table = new DataTable(); table.Columns.Add(keywords ? "Keyword" : "Url", typeof(string));
    if (!keywords) table.Columns.Add("MaxDepth", typeof(int));
    foreach (var value in values)
    {
        var row = table.NewRow(); row[0] = value; if (!keywords) row[1] = 2; table.Rows.Add(row);
    }
    return table;
}
void Invalid(DataTable rows, bool keywords, string name)
{
    try { validate.Invoke(null, new object[] { rows, keywords }); throw new Exception("Validation accepted invalid values"); }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { Check(true, name); }
}
Invalid(Rows(false, "file:///C:/test"), false, "seed editor rejects non-HTTP URL");
Invalid(Rows(true, "   "), true, "keyword editor rejects blanks");
Invalid(Rows(true, "Example", "example"), true, "keyword editor rejects duplicate terms");
validate.Invoke(null, new object[] { Rows(false, "https://example.com/A", "https://example.com/a"), false });
Check(true, "seed editor preserves distinct URL path case");
var logDirectory = Path.Combine(Path.GetTempPath(), "ButchCrawlerTests_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(logDirectory);
try
{
    var path = Path.Combine(logDirectory, "butchcrawler-test.log");
    await File.WriteAllTextAsync(path, "first\nsecond\n");
    var before = await File.ReadAllBytesAsync(path);
    var tail = await LogFiles.ReadTailAsync(path);
    Check(tail.Text == "first\nsecond\n" && !tail.Truncated && before.SequenceEqual(await File.ReadAllBytesAsync(path)), "log read preserves file bytes");
    await File.WriteAllTextAsync(path, new string('x', 2 * 1024 * 1024) + "\nlast line\n");
    tail = await LogFiles.ReadTailAsync(path);
    Check(tail.Truncated && tail.Text.EndsWith("last line\n") && tail.Text.Length <= 2 * 1024 * 1024, "large log tail is bounded and includes latest line");
    using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
    {
        try { await LogFiles.DeleteAsync(path); throw new Exception("Deleted an in-use log"); }
        catch (IOException) { Check(File.Exists(path), "in-use log is retained"); }
    }
    await LogFiles.DeleteAsync(path);
    Check(!File.Exists(path), "closed log can be deleted");
}
finally { Directory.Delete(logDirectory, recursive: true); }
await ReviewRegressionTests.RunAsync(Check);
await CrawlFailureTests.RunAsync(Check);
KeySettingsTests.Run(Check);
SettingsTests.Run(Check);
ActivityTests.Run(Check);
CollectionSearchQueryTests.Run(Check);
ResetDialogTests.Run(Check);
StarterDatabaseTests.Run(Check);
var localText = new LocalTextProcessor();
var localPlain = await localText.ProcessAsync("test.txt", Encoding.UTF8.GetBytes("Butch Cassidy and Etta Place"), null, ".txt", default);
Check(localPlain.Text.Contains("Butch Cassidy") && CrawlerService.KeywordMatchLine("test.txt", localPlain.Text, "Etta Place") != null, "local text extraction feeds literal keyword matching");
var localHtml = await localText.ProcessAsync("test.htm", Encoding.UTF8.GetBytes("<p>Etta &amp; Butch Cassidy</p><script>hidden keyword</script>"), null, ".htm", default);
Check(localHtml.Text.Contains("Etta & Butch Cassidy") && !localHtml.Text.Contains("hidden keyword"), "local HTML decodes entities and excludes scripts");
var archivedHtml = Convert.ToBase64String(Encoding.UTF8.GetBytes("<html><head><title>Archived History</title></head><body><p>Etta &amp; Butch Cassidy</p><script>hidden archive script</script></body></html>"));
var mhtBytes = Encoding.ASCII.GetBytes("MIME-Version: 1.0\r\nSubject: Saved page\r\nContent-Type: multipart/related; type=\"text/html\"; boundary=\"archive-boundary\"\r\n\r\n--archive-boundary\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\nContent-Location: https://example.test/history\r\n\r\n" + archivedHtml + "\r\n--archive-boundary--\r\n");
var mhtResult = await new MhtProcessor().ProcessAsync("history.mht", mhtBytes, null, ".mht", default);
Check(mhtResult.Title == "Archived History" && mhtResult.Text.Contains("Etta & Butch Cassidy") && !mhtResult.Text.Contains("hidden archive script"), "MHT extracts archived HTML safely without scripts");
await AudioTests.RunAsync(Check);
await AiTests.RunAsync(Check);
await BillingTests.RunAsync(Check);
var leaseRenewals = 0;
await using (var heartbeat = new QueueLeaseHeartbeat(() => { Interlocked.Increment(ref leaseRenewals); return Task.FromResult(true); }, default, TimeSpan.FromMilliseconds(10)))
{
    await Task.Delay(80);
    Check(leaseRenewals > 0 && !heartbeat.Token.IsCancellationRequested, "whole-crawl heartbeat renews ownership during slow work");
}
var stoppedRenewals = leaseRenewals;
await Task.Delay(40);
Check(leaseRenewals == stoppedRenewals, "whole-crawl heartbeat stops when operation finishes");
await using (var heartbeat = new QueueLeaseHeartbeat(() => Task.FromResult(false), default, TimeSpan.FromMilliseconds(10)))
{
    try { await Task.Delay(2000, heartbeat.Token); } catch (OperationCanceledException) { }
    Check(heartbeat.OwnershipLost && heartbeat.Token.IsCancellationRequested, "lost crawl lease cancels pending network work");
}
using (var cancelled = new CancellationTokenSource())
await using (var heartbeat = new QueueLeaseHeartbeat(() => Task.FromResult(true), cancelled.Token))
{
    cancelled.Cancel();
    Check(heartbeat.Token.IsCancellationRequested && !heartbeat.OwnershipLost, "user stop propagates independently of lease loss");
}
Check(typeof(SqlAiRepository).Assembly.GetManifestResourceNames().Contains("AiResearchSchema.sql"), "AI initialization schema is embedded in compiled application");
Console.WriteLine($"All {passed} behavioral checks passed.");
