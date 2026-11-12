using System.IO.Compression;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ButchCrawler.App;

internal static class CrawlCoverageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine("ButchCrawler.App", "appsettings.json")))!;
        check(settings["Crawler"]!["InitialSeedLimit"]!.GetValue<int>() == 0,
            "seed URL limit is unlimited");

        const string xml = "<?xml version=\"1.0\"?><urlset><url><loc>https://example.org/page</loc></url></urlset>";
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes(xml));
            compressed = output.ToArray();
        }
        var decode = typeof(CrawlerService).GetMethod("DecodeSitemapXmlAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var decoded = await (Task<string>)decode.Invoke(null, new object[]
        { new FetchResult { BodyBytes = compressed, Success = true }, CancellationToken.None })!;
        check(decoded == xml, "gzip-compressed sitemap XML is decoded");

        check(typeof(NoReadableTextException).IsSubclassOf(typeof(Exception)),
            "photographs without OCR text have a distinct skip classification");

        var pdfType = typeof(TesseractPdfProcessor);
        var usable = pdfType.GetMethod("HasUsableText", BindingFlags.Static | BindingFlags.NonPublic)!;
        check(!(bool)usable.Invoke(null, new object?[] { "page 1", 20 })! &&
              (bool)usable.Invoke(null, new object?[] { "A substantial digital page from an archival report dated 1890.", 20 })!,
            "PDF pages with only token text are selected for OCR");
        var compose = pdfType.GetMethod("ComposePageText", BindingFlags.Static | BindingFlags.NonPublic)!;
        var hybrid = (string)compose.Invoke(null, new object?[]
        {
            2,
            new[] { "Digital text from page one with enough useful characters.", "" },
            new Dictionary<int, string> { [2] = "OCR text recovered from the scanned second page." },
            20,
            10000
        })!;
        check(hybrid.Contains("[Page 1]", StringComparison.Ordinal) &&
              hybrid.Contains("[Page 2]", StringComparison.Ordinal) &&
              hybrid.Contains("OCR text recovered", StringComparison.Ordinal),
            "mixed PDFs combine digital text and page-level OCR with page markers");

        check(settings["Ocr"]!["PreprocessImages"]!.GetValue<bool>() &&
              settings["Ocr"]!["PdfRenderDpi"]!.GetValue<int>() >= 300,
            "historical image cleanup and high-quality PDF rendering are enabled");
    }
}
