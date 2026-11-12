using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ButchCrawler.App;
using ButchCrawler.Operator;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

internal static class AiTests
{
    private const string Result = """
    {"answer":"Mary visited Helena in 1880.","citations":[{"source_id":"S1","quote":"Mary visited Helena in 1880."}],"findings":[{"kind":"person","name":"Mary","source_id":"S1","quote":"Mary visited Helena in 1880."}],"uncertainties":[],"needs_review":false,"insufficient_evidence":false}
    """;
    private static IConfiguration Settings(params (string, string?)[] changes)
    {
        var values = new Dictionary<string, string?> { ["AI:Enabled"] = "true", ["AI:HandwritingEnabled"] = "true", ["OpenAI:ApiKey"] = "test-only", ["AI:MaxAttempts"] = "1" };
        foreach (var (k,v) in changes) values[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text = json } } } } })) };
    private sealed class Memory : IAiRepository
    {
        public List<AiSavedResult> Rows = new(); public int Requests;
        private readonly SemaphoreSlim _gate = new(1);
        private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable { public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; } }
        public async Task<IAsyncDisposable> AcquireAiWorkAsync(string key, CancellationToken ct) { await _gate.WaitAsync(ct); return new Lease(_gate); }
        public Task<bool> ConsumeAiRequestAsync(int limit, CancellationToken ct) => Task.FromResult(++Requests <= limit);
        public Task<AiSavedResult?> GetAiResultAsync(string task,string source,string hash,string model,CancellationToken ct) => Task.FromResult(Rows.FirstOrDefault(r=>r.Task==task && r.Source==source && r.InputHash==hash && r.Model==model));
        public Task<AiSavedResult> SaveAiResultAsync(string task,string source,string hash,string model,string text,string json,CancellationToken ct)
        { var row = new AiSavedResult(Rows.Count+1,task,source,hash,model,text,json,false,null,DateTime.UtcNow); Rows.Add(row); return Task.FromResult(row); }
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        var store = new Memory(); var calls = 0;
        using var http = new HttpClient(new Handler(async (request,ct) =>
        {
            calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            check(request.RequestUri!.AbsoluteUri == "https://api.openai.com/v1/responses" && request.Headers.Authorization?.Parameter == "test-only", "AI uses Responses endpoint and configured key");
            check(!root.GetProperty("store").GetBoolean() && root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean() && !root.TryGetProperty("reasoning",out _), "AI requests strict schema without unsupported reasoning parameter");
            return Response(Result);
        }));
        var config = Settings(); var service = new AiResearchService(new AiJsonClient(http,config,store),config,store);
        var analysis = await service.AnalyzeAsync("https://example.test/diary", "Mary visited Helena in 1880.",default);
        check(AiResearchService.ParseResult(analysis).NeedsReview && AiResearchService.ParseResult(analysis).Citations[0].Source == analysis.Source, "AI analysis preserves source evidence and requires review");
        await service.AnalyzeAsync(analysis.Source,"Mary visited Helena in 1880.",default);
        check(calls==1,"AI summaries and extraction reuse cached analysis");
        var sources = new[] { new AiSource("S1",analysis.Source,"Mary visited Helena in 1880.") };
        await service.AnswerAsync("Where did Mary travel?",sources,default);
        await service.AnswerAsync("Where did Mary travel?",sources,default);
        check(calls==2 && store.Rows.Count==2,"AI collection answer persists and reuses identical evidence");
        try { AiResearchService.ValidateResult(Result.Replace("Mary visited Helena in 1880.","Invented evidence"),sources); throw new Exception("Expected invalid citation"); }
        catch(AiResearchException) { check(true,"AI fabricated quotations rejected"); }
        try { await service.AnswerAsync("Where?",Array.Empty<AiSource>(),default); throw new Exception("Expected no sources"); }
        catch(AiResearchException) { check(calls==2,"AI empty retrieval sends no request"); }
        var disabled = Settings(("AI:Enabled","false"));
        try { await new AiResearchService(new AiJsonClient(http,disabled,store),disabled,store).AnalyzeAsync("new","text",default); throw new Exception("Expected disabled"); }
        catch(AiResearchException) { check(calls==2,"AI disabled state blocks new requests"); }
        var quota = new Memory { Requests = 50 };
        try { await new AiResearchService(new AiJsonClient(http,config,quota),config,quota).AnswerAsync("Where?",sources,default); throw new Exception("Expected quota"); }
        catch(AiResearchException) { check(calls==2,"AI quota prevents HTTP request"); }
        using var vision = new HttpClient(new Handler(async (r,ct) =>
        {
            using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(ct));
            check(body.RootElement.GetProperty("input")[0].GetProperty("content")[1].GetProperty("image_url").GetString()!.StartsWith("data:image/png;base64,"),"AI handwriting uploads selected image inline");
            return Response("{\"transcription\":\"Mary [?]\",\"uncertainties\":[\"surname unclear\"],\"needs_review\":false}");
        }));
        var handwritten = await new AiResearchService(new AiJsonClient(vision,config,store),config,store).TranscribeImageAsync("scan.png",new byte[]{1,2},"image/png",default);
        check(AiResearchService.ParseHandwriting(handwritten).NeedsReview && AiResearchService.ParseHandwriting(handwritten).Uncertainties.Count==1,"AI handwriting saves uncertainty and forces review");
        using var cancelHttp = new HttpClient(new Handler(async (_,ct) => { await Task.Delay(10000,ct); return Response(Result); }));
        using var cancel = new CancellationTokenSource(30);
        try { await new AiJsonClient(cancelHttp,config,new Memory()).GenerateAsync("gpt-4.1-mini","test",Array.Empty<object>(),"test",AiResearchService.ResultSchema(),cancel.Token); throw new Exception("Expected cancel"); }
        catch(OperationCanceledException) { check(true,"AI cancellation stops pending HTTP request"); }
        using var denied = new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        try { await new AiJsonClient(denied,config,new Memory()).GenerateAsync("model","test",Array.Empty<object>(),"test",AiResearchService.ResultSchema(),default); throw new Exception("Expected 401"); }
        catch(AiResearchException ex) { check(!ex.Retryable && !ex.Message.Contains("test-only"),"AI authentication errors are actionable and redact key"); }
    }

    public static async Task RunSqlAsync()
    {
        var config = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App")).AddJsonFile("appsettings.json").AddEnvironmentVariables().Build();
        var builder = new SqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection"));
        var database = "ButchCrawlerAiTest_" + Guid.NewGuid().ToString("N");
        var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
        await using var admin = new SqlConnection(master.ConnectionString); await admin.OpenAsync();
        async Task Exec(SqlConnection c,string sql) { using var cmd = c.CreateCommand(); cmd.CommandText=sql; await cmd.ExecuteNonQueryAsync(); }
        void Check(bool ok,string name) { if(!ok) throw new Exception("FAIL: "+name); Console.WriteLine("PASS: "+name); }
        await Exec(admin,$"CREATE DATABASE [{database}]");
        try
        {
            builder.InitialCatalog=database;
            var repo = new SqlAiRepository(builder.ConnectionString);
            await repo.InitializeAsync(default); await repo.InitializeAsync(default);
            await using(var c = new SqlConnection(builder.ConnectionString))
            {
                await c.OpenAsync(); await Exec(c,"CREATE TABLE dbo.CrawlResults(Url nvarchar(2000),content nvarchar(max),status_code int NOT NULL DEFAULT 200); CREATE TABLE dbo.documents(url nvarchar(500),[text] nvarchar(max)); CREATE TABLE dbo.AudioTranscripts(Id int IDENTITY,Source nvarchar(2000),Transcript nvarchar(max),IsComplete bit NOT NULL DEFAULT 1,TranscribedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME()); INSERT dbo.CrawlResults(Url,content) VALUES(N'https://example.test/Mary',N'Mary visited Helena in 1880.'); INSERT dbo.documents VALUES(N'local.pdf',N'John reached Montana in 1890.'); INSERT dbo.AudioTranscripts(Source,Transcript) VALUES(N'talk.wav',N'Mary recalls Helena.');");
            }
            Check((await repo.SearchAsync("",default)).Count==3,"AI source browser handles blank query across all source tables");
            Check((await repo.SearchAsync("What did Mary say about Helena?",default)).Count==2,"AI question retrieval matches specific terms across saved text and audio");
            Check((await repo.SearchAsync("Mary AND Helena",default)).Count==2,"collection SQL search requires both AND terms");
            Check((await repo.SearchAsync("Mary OR John",default)).Count==3,"collection SQL search accepts either OR term");
            Check((await repo.SearchAsync("Mary NOT Helena",default)).Count==0,"collection SQL search excludes NOT terms");
            Check((await repo.SearchAsync("(Mary OR John) AND 1890",default)).Single().Source=="local.pdf","collection SQL search honors parentheses");
            Check((await repo.SearchAsync("\"Mary visited\"",default)).Single().Source=="https://example.test/Mary","collection SQL search honors quoted phrases");
            Check((await repo.SearchAsync("'; DROP TABLE dbo.documents; --",default)).Count==0,"AI retrieval parameterizes query text");
            Check(await repo.ReadDocumentAsync("local.pdf","document",default)=="John reached Montana in 1890.","AI reads saved PDF text");
            await using (var c = new SqlConnection(builder.ConnectionString))
            {
                await c.OpenAsync();
                await Exec(c,"CREATE TABLE dbo.StructuredDataSources(Source nvarchar(2000)); INSERT dbo.CrawlResults(Url,content) VALUES(N'https://archive.test/export.csv',N'Cheyenne register, 1902'); INSERT dbo.StructuredDataSources VALUES(N'https://archive.test/export.csv');");
            }
            var structured = await repo.SearchAsync("Cheyenne", default);
            Check(structured.Count == 1 && structured[0].Kind == "structured", "collection search identifies structured datasets without duplicate webpage results");
            Check(await repo.ReadDocumentAsync(structured[0].Source, "structured", default) == "Cheyenne register, 1902", "collection search opens indexed structured dataset text");
            await using (var c = new SqlConnection(builder.ConnectionString))
            {
                await c.OpenAsync();
                await Exec(c,"INSERT dbo.CrawlResults(Url,content,status_code) VALUES(N'failed',N'UniqueFailedEvidence',500); INSERT dbo.AudioTranscripts(Source,Transcript,IsComplete) VALUES(N'partial',N'UniquePartialEvidence',0);");
            }
            Check((await repo.SearchAsync("UniqueFailedEvidence UniquePartialEvidence",default)).Count==0,"AI retrieval excludes failed pages and unfinished audio");
            try { await repo.ReadDocumentAsync("partial","audio",default); throw new Exception("Expected incomplete transcript rejection"); }
            catch(AiResearchException) { Check(true,"AI cannot read incomplete audio by direct source selection"); }
            var hash=AiResearchService.HashText("input");
            await using(var lease = await repo.AcquireAiWorkAsync("test",default))
                await repo.SaveAiResultAsync(AiResearchService.AnalysisTask,"source",hash,"gpt-4.1-mini","summary",Result,default);
            var saved = await repo.GetAiResultAsync(AiResearchService.AnalysisTask,"source",hash,"gpt-4.1-mini",default);
            Check(saved is not null && saved.CreatedAtUtc.Kind==DateTimeKind.Utc && (await repo.HistoryAsync(default)).Count==1,"AI SQL cache and history preserve result and UTC timestamp");
            var quota = await Task.WhenAll(Enumerable.Range(0,12).Select(_=>repo.ConsumeAiRequestAsync(5,default)));
            Check(quota.Count(b=>b)==5,"AI SQL daily request limit is atomic under concurrency");
            await using(var lease = await repo.AcquireAiWorkAsync("reuse",default)) { }
            await using(var lease = await repo.AcquireAiWorkAsync("reuse",default)) { Check(true,"AI SQL work lease releases before pooled connection reuse"); }
            var folder = Path.Combine(Path.GetTempPath(), "ButchCrawlerAiWorkspace_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var path = Path.Combine(folder,"appsettings.json");
                await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new { ConnectionStrings = new { DefaultConnection = builder.ConnectionString }, AI = new { Enabled=true,HandwritingEnabled=true }, OpenAI = new { ApiKey="test-only" } }));
                var image = Path.Combine(folder,"page.png"); await File.WriteAllBytesAsync(image,new byte[]{1,2,3});
                var requests = 0;
                using var mock = new HttpClient(new Handler(async (request,ct) =>
                {
                    requests++;
                    var body=await request.Content!.ReadAsStringAsync(ct);
                    return body.Contains("input_image") ? Response("{\"transcription\":\"Mary [?]\",\"uncertainties\":[\"surname unclear\"],\"needs_review\":true}") : Response(Result);
                }));
                using var workspace = new SqlAiResearchWorkspace(path,mock);
                var docs = await workspace.SearchAsync("Helena",default);
                var document=docs.Single(d=>d.Id=="crawl");
                Check((await workspace.ReadDocumentAsync(document,default)).Contains("1880"),"AI workspace loads selected original source");
                var summary=await workspace.RunAsync(new(AiResearchFeature.Summary,"",document,null),default);
                var entities=await workspace.RunAsync(new(AiResearchFeature.Entities,"",document,null),default);
                Check(requests==1 && summary.Text.Contains("Mary visited") && entities.Text.Contains("person: Mary") && summary.Sources.Single().Source==document.Source,"AI workspace summary and entity tasks share cached analysis with source links");
                var answer=await workspace.RunAsync(new(AiResearchFeature.Question,"Where did Mary visit in 1880?",null,null),default);
                Check(answer.Text.Contains("Mary visited") && answer.Sources.Count==1,"AI workspace retrieves passages and saves collection answer");
                var handwriting=await workspace.RunAsync(new(AiResearchFeature.Handwriting,"",null,image),default);
                Check(handwriting.Text.Contains("surname unclear") && handwriting.Sources.Single().Source==image,"AI workspace saves handwriting and uncertainty with original image reference");
                var history=await workspace.HistoryAsync(default);
                Check(history.Count==4 && history.Any(r=>r.Id==handwriting.Id && r.CreatedAt.Kind==DateTimeKind.Utc),"AI workspace reloads all persisted tasks with dates");
            }
            finally { Directory.Delete(folder,true); }
        }
        finally { SqlConnection.ClearAllPools(); await Exec(admin,$"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]"); }
    }
}
