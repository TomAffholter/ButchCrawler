using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ButchCrawler.App;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DiscoveryReviewTest
{
    private sealed class Browser : IPlaywrightQueue
    { public Task<RenderResult> EnqueueRenderAsync(string url,int timeout,bool saveScreenshot,bool saveTrace,CancellationToken cancellationToken=default) => throw new Exception("Unexpected browser request"); }
    private sealed class Robots : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("User-agent: *\nAllow: /\n") }); }

    public static async Task RunAsync(SqlConnection connection, SqlStorage storage, IEmbeddingIndexQueue queue, IPdfProcessor pdf, Action<bool,string> check)
    {
        using var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
        var origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var childRequests=0;
        var server=Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    using var stream = client.GetStream(); using var reader = new StreamReader(stream,leaveOpen:true);
                    var line = await reader.ReadLineAsync(lifetime.Token); var path=line!.Split(' ')[1];
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(lifetime.Token))) { }
                    if(path=="/child") Interlocked.Increment(ref childRequests);
                    var body="<html><head><title>History archive</title></head><body><main>"+string.Concat(Enumerable.Repeat("Butch Cassidy historical source text. ",30))+(path=="/seed"?"<a href='/child'>Child</a><a href='/child'>Duplicate</a>":"")+"</main></body></html>";
                    var bytes=Encoding.UTF8.GetBytes(body);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"),lifetime.Token);
                    await stream.WriteAsync(bytes,lifetime.Token);
                }
            }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        });
        try
        {
            using(var command=connection.CreateCommand())
            {
                command.CommandText=@"CREATE TABLE dbo.SchemaVersion(Version int); INSERT dbo.SchemaVersion VALUES(13);
CREATE TABLE dbo.SeedUrls(Id int IDENTITY PRIMARY KEY,Url nvarchar(2000),Domain nvarchar(200),Category nvarchar(100),Priority int,MaxDepth int,FollowExternal bit,IsActive bit,Notes nvarchar(max),CreatedAt datetime2,LastCrawledAt datetime2,SourceType nvarchar(100),DiscoverySource nvarchar(100),Tags nvarchar(100));
INSERT dbo.SeedUrls(Url,Priority,MaxDepth,FollowExternal,IsActive,CreatedAt) VALUES(@seed,1,2,0,1,SYSUTCDATETIME());
CREATE TABLE dbo.CrawlQueue(Id bigint IDENTITY PRIMARY KEY,Url nvarchar(2000),SeedId int,SeedUrl nvarchar(2000),ParentUrl nvarchar(2000),Depth int,Status nvarchar(50),created_at datetime2,updated_at datetime2,locked_until datetime2,LockToken uniqueidentifier,Attempts int DEFAULT 0,LastError nvarchar(max));
ALTER TABLE dbo.VisitedUrls ADD last_crawled datetime2,updated_at datetime2;";
                command.Parameters.AddWithValue("@seed",origin+"/seed"); await command.ExecuteNonQueryAsync();
            }
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Crawler:AllowPrivateNetworks"]="true",["Crawler:MaxPages"]="10",["Crawler:MaxDepth"]="2",["Crawler:MaxConcurrency"]="1",["Crawler:MinDelayMs"]="0",["Indexer:Enabled"]="false" }).Build();
            using var http = new HttpClient(new Robots());
            using var robots = new RobotsManager(http,NullLogger<RobotsManager>.Instance,config);
            var metrics=new MetricsService(); var fetcher=new Fetcher(new HttpClient(),NullLogger<Fetcher>.Instance,config,new NoopCaptchaSolver());
            CrawlerService Worker() => new(NullLogger<CrawlerService>.Instance,config,fetcher,new HtmlParser(),storage,robots,new HostRateLimiter(1,TimeSpan.Zero),new PayloadProcessorRegistry(),pdf,queue,new Browser(),metrics,new CrawlerStartupSignal());
            using(var worker=Worker()) { await worker.StartAsync(lifetime.Token); await worker.ExecuteTask!.WaitAsync(lifetime.Token); }
            check(childRequests==1,"newly discovered child is fetched once instead of skipped as already visited");
            using(var command=connection.CreateCommand()) { command.CommandText="SELECT COUNT(*) FROM dbo.CrawlQueue WHERE Status='completed'"; check(Convert.ToInt32(await command.ExecuteScalarAsync())==2,"parent and discovered child both complete durably"); }
            using(var worker=Worker()) { await worker.StartAsync(lifetime.Token); await worker.ExecuteTask!.WaitAsync(lifetime.Token); }
            check(childRequests==1,"restart retains completed child and does not fetch it again");
        }
        finally { lifetime.Cancel(); listener.Stop(); try { await server; } catch(SocketException) { } }
    }
}
