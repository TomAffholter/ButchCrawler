using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;

namespace ButchCrawler.App;

public sealed class LocalTextProcessor : IPayloadProcessor
{
    public async Task<ProcessedPayload> ProcessAsync(string url, byte[] body, string? contentType, string? extension, CancellationToken ct)
    {
        using var stream = new MemoryStream(body);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        if (extension is not null && (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)))
        {
            var document = new HtmlDocument(); document.LoadHtml(text);
            foreach (var node in document.DocumentNode.SelectNodes("//script|//style") ?? Enumerable.Empty<HtmlNode>()) node.Remove();
            text = HtmlEntity.DeEntitize(document.DocumentNode.InnerText);
        }
        return new ProcessedPayload { Text = text, ContentHash = SHA256.HashData(body), Title = Path.GetFileName(url) };
    }
}
