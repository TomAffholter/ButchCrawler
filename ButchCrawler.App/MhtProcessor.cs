using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MimeKit;

namespace ButchCrawler.App;

/// <summary>Reads an MHT/MHTML web archive without rendering it or loading external resources.</summary>
public sealed partial class MhtProcessor : IPayloadProcessor
{
    private const int MaxExtractedCharacters = 50_000_000;

    public Task<ProcessedPayload> ProcessAsync(
        string url,
        byte[] body,
        string? contentType,
        string? extension,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (body.LongLength > 50_000_000) throw new InvalidDataException("The web archive exceeds the secure 50 MB processing limit.");
        using var stream = new MemoryStream(body, writable: false);
        var message = MimeMessage.Load(stream, ct);

        var html = message.HtmlBody;
        var plainText = message.TextBody;
        string text;
        string? title = null;

        if (!string.IsNullOrWhiteSpace(html))
        {
            var document = new HtmlDocument();
            document.LoadHtml(html);
            title = HtmlEntity.DeEntitize(document.DocumentNode.SelectSingleNode("//title")?.InnerText ?? string.Empty).Trim();
            foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//template") ?? Enumerable.Empty<HtmlNode>())
                node.Remove();
            text = NormalizeWhitespace(HtmlEntity.DeEntitize(document.DocumentNode.InnerText));
        }
        else
        {
            text = NormalizeWhitespace(plainText ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("The web archive does not contain a readable HTML or text page.");
        if (text.Length > MaxExtractedCharacters)
            throw new InvalidDataException($"The web archive expands to more than {MaxExtractedCharacters:N0} text characters.");

        if (string.IsNullOrWhiteSpace(title)) title = message.Subject?.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileName(url);

        return Task.FromResult(new ProcessedPayload
        {
            Text = text,
            Title = title,
            ContentHash = SHA256.HashData(body)
        });
    }

    private static string NormalizeWhitespace(string value) => Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
