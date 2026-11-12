using System.IO;
using System.Net.Http;
using System.Reflection;
using ButchCrawler.App;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class CrawlSqlTests
{
    private sealed class Pdf(bool fail=false) : IPdfProcessor
    {
        public Task<(string Text,byte[] ExtractedHash)> ExtractTextAsync(Stream stream,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if(fail) throw new InvalidDataException("Fixture extraction failed");
            return Task.FromResult(("Butch Cassidy met BUTCH CASSIDY.",new byte[]{1,2,3}));
        }
    }
    private sealed class Browser : IPlaywrightQueue
    { public Task<RenderResult> EnqueueRenderAsync(string url,int timeout,bool saveScreenshot,bool saveTrace,CancellationToken cancellationToken=default) => throw new Exception("Unexpected browser request"); }
    public static async Task RunAsync()
    {
        var settings=new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App")).AddJsonFile("appsettings.json").AddEnvironmentVariables().Build();
        var target=new SqlConnectionStringBuilder(settings.GetConnectionString("DefaultConnection"));
        var database="ButchCrawlerCrawlTest_"+Guid.NewGuid().ToString("N");
        var master=new SqlConnectionStringBuilder(target.ConnectionString){InitialCatalog="master"};
        await using var admin=new SqlConnection(master.ConnectionString); await admin.OpenAsync();
        async Task Exec(SqlConnection c,string sql) { using var cmd=c.CreateCommand(); cmd.CommandText=sql; await cmd.ExecuteNonQueryAsync(); }
        void Check(bool ok,string label) { if(!ok) throw new Exception("FAIL: "+label); Console.WriteLine("PASS: "+label); }
        await Exec(admin,$"CREATE DATABASE [{database}]");
        try
        {
            target.InitialCatalog=database;
            var storage=new SqlStorage(target.ConnectionString,NullLogger<SqlStorage>.Instance);
            try { await storage.GetKeywordsAsync(throwOnError:true); throw new Exception("Expected missing keyword table"); }
            catch(SqlException) { Check(true,"keyword loading fails explicitly instead of silently starting without search terms"); }
            await using var connection=new SqlConnection(target.ConnectionString); await connection.OpenAsync();
            await Exec(connection,"CREATE TABLE dbo.SeedKeywords(Id int IDENTITY PRIMARY KEY,Keyword nvarchar(200),IsActive bit); INSERT dbo.SeedKeywords VALUES(N'Butch Cassidy',1),(N'Helena',0); CREATE TABLE dbo.CrawlResults(Url nvarchar(2000),status_code int,content nvarchar(max),created_at datetime2,updated_at datetime2); CREATE TABLE dbo.VisitedUrls(url nvarchar(2000),content_hash nvarchar(100),status_code int,visited_at datetime2);");
            await Exec(connection,(await File.ReadAllTextAsync("Scripts/010_CreateDocumentKeywordHits.sql")).Replace("USE ButchCrawlerDB;",""));
            var keywords=await storage.GetKeywordsAsync(true);
            Check(keywords.SequenceEqual(new[]{"Butch Cassidy"}),"only active research keywords load");
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Crawler:AllowPdf"]="true",["Indexer:Enabled"]="false",["OpenAI:ApiKey"]="test-only" }).Build();
            using var http=new HttpClient(); using var robots=new RobotsManager(http,NullLogger<RobotsManager>.Instance,config);
            using var queue=new EmbeddingIndexQueue(1); var metrics=new MetricsService();
            var fetcher=new Fetcher(http,NullLogger<Fetcher>.Instance,config,new NoopCaptchaSolver());
            CrawlerService Worker(IPdfProcessor pdf) => new(NullLogger<CrawlerService>.Instance,config,fetcher,new HtmlParser(),storage,robots,new HostRateLimiter(1,TimeSpan.Zero),new PayloadProcessorRegistry(),pdf,queue,new Browser(),metrics,new CrawlerStartupSignal());
            var handle=typeof(CrawlerService).GetMethod("TryHandlePdfAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var pdfResult=new FetchResult { IsPdf=true,Success=true,StatusCode=200,BodyBytes=new byte[]{1},FinalUrl="https://example.test/diary.pdf" };
            Task<bool> Process(CrawlerService worker,CancellationToken ct=default) => (Task<bool>)handle.Invoke(worker,new object?[]{null,null,pdfResult.FinalUrl,pdfResult,null,ct})!;
            var output=new StringWriter(); var original=Console.Out;
            try { Console.SetOut(output); Check(await Process(Worker(new Pdf())),"PDF handler finishes"); }
            finally { Console.SetOut(original); }
            Check(output.ToString().Contains("[CRAWL-MATCH] Butch Cassidy (2) | https://example.test/diary.pdf"),"PDF handler emits visible keyword matches with indexing disabled");
            using(var cmd=connection.CreateCommand())
            { cmd.CommandText="SELECT MatchCount FROM dbo.CrawlKeywordHits"; Check(Convert.ToInt32(await cmd.ExecuteScalarAsync())==2,"PDF visible count agrees with persisted keyword occurrences"); }
            Check(CrawlerService.KeywordMatchLine("url","unrelated historical text","Butch Cassidy")==null,"unrelated source produces no false keyword match");
            try { await Process(Worker(new Pdf(true))); throw new Exception("Expected PDF failure"); }
            catch(InvalidDataException) { Check(true,"PDF extraction failure propagates instead of reporting completed content"); }
            using var canceled=new CancellationTokenSource(); canceled.Cancel();
            try { await Process(Worker(new Pdf()),canceled.Token); throw new Exception("Expected cancellation"); }
            catch(OperationCanceledException) { Check(true,"PDF cancellation propagates instead of reporting completed content"); }
            await DetailedReviewSqlTests.RunAsync(connection, storage, queue, new Pdf(), Check);
        }
        finally { SqlConnection.ClearAllPools(); await Exec(admin,$"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]"); }
    }
}
