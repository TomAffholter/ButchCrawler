using System.IO;
using ButchCrawler.App;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class DetailedReviewSqlTests
{
    private sealed class Document : IPayloadProcessor
    {
        public Task<ProcessedPayload> ProcessAsync(string url, byte[] body, string? mime, string? extension, CancellationToken ct) =>
            Task.FromResult(new ProcessedPayload { Text = url.Contains("empty") ? "" : "Butch Cassidy document", ContentHash = new byte[] { 1 } });
    }

    public static async Task RunAsync(SqlConnection connection, SqlStorage storage, IEmbeddingIndexQueue queue, IPdfProcessor pdf, Action<bool,string> check)
    {
        async Task Exec(string sql) { using var cmd = connection.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
        async Task<int> Count(string sql) { using var cmd = connection.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt32(await cmd.ExecuteScalarAsync()); }
        var root = Path.Combine(Path.GetFullPath("Tests/work"), "local-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,"test.txt"), "Butch Cassidy text");
            await File.WriteAllTextAsync(Path.Combine(root,"test.pdf"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(root,"test.docx"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(root,"empty.docx"), "fixture");
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["LocalCrawler:RootPath"]=root,["LocalCrawler:MaxConcurrency"]="2",["Indexer:Enabled"]="false" }).Build();
            var registry = new PayloadProcessorRegistry(); registry.RegisterExtension(".docx",new Document());
            var output = new StringWriter(); var previous = Console.Out;
            try
            {
                Console.SetOut(output);
                using var worker = new LocalFileCrawler(registry,storage,queue,pdf,config,NullLogger<LocalFileCrawler>.Instance);
                await worker.StartAsync(default); await worker.ExecuteTask!;
            }
            finally { Console.SetOut(previous); }
            check(output.ToString().Contains("Finished: 3 saved, 1 failed, 0 skipped"),"real local scan reports saved and failed files separately");
            check(await Count("SELECT COUNT(*) FROM dbo.CrawlResults WHERE Url LIKE '%local-review-%'")==3,"local text, document and PDF persist with indexing disabled; empty extraction does not");
            check(await Count("SELECT SUM(MatchCount) FROM dbo.CrawlKeywordHits WHERE Url LIKE '%local-review-%'")==4,"local keyword rows match extracted text for all supported processing paths");
            var source=Path.Combine(root,"test.txt");
            await storage.SaveLocalTextAsync(source,"Unrelated source",default);
            check(await Count("SELECT COUNT(*) FROM dbo.CrawlKeywordHits WHERE Url LIKE '%local-review-%test.txt'")==0,"editing local content removes obsolete keyword matches");
            await Exec("CREATE TRIGGER RejectKeywordWrite ON dbo.CrawlKeywordHits AFTER INSERT,UPDATE AS THROW 51000,'Fixture write failure',1;");
            try { await storage.SaveLocalTextAsync(source,"Butch Cassidy changed",default); throw new Exception("Expected failed keyword write"); }
            catch(SqlException) { check(true,"local keyword write error is propagated"); }
            check(await Count("SELECT COUNT(*) FROM dbo.CrawlResults WHERE Url LIKE '%local-review-%test.txt' AND content=N'Unrelated source'")==1,"failed keyword write rolls back text update");
            await Exec("DROP TRIGGER RejectKeywordWrite;");
            await Exec("CREATE TRIGGER RejectResultWrite ON dbo.CrawlResults AFTER INSERT,UPDATE AS THROW 51001,'Fixture result failure',1;");
            try { await storage.StoreCrawlResultAsync(source,200,"new"); throw new Exception("Expected failed result write"); }
            catch(SqlException) { check(true,"simple result save never swallows database failure"); }
            try { await storage.StoreCrawlResultAsync(source,200,"new",DateTime.UtcNow,null,null,null,null); throw new Exception("Expected failed rich result write"); }
            catch(SqlException) { check(true,"document result save never swallows database failure"); }
            await Exec("DROP TRIGGER RejectResultWrite;");
            await DiscoveryReviewTest.RunAsync(connection, storage, queue, pdf, check);
        }
        finally { Directory.Delete(root,true); }
    }
}
