using UglyToad.PdfPig;

namespace ButchCrawler.App;

public static class SecurePdfValidator
{
    public static void Validate(Stream stream, int maxPages, int maxCharacters)
    {
        if (!stream.CanSeek) throw new InvalidDataException("PDF security validation requires a seekable stream.");
        var original = stream.Position;
        try
        {
            stream.Position = 0;
            using var document = PdfDocument.Open(stream);
            var pages = 0; long characters = 0;
            foreach (var page in document.GetPages())
            {
                if (++pages > maxPages) throw new InvalidDataException($"PDF exceeds the secure {maxPages:N0}-page limit.");
                characters += page.Text?.Length ?? 0;
                if (characters > maxCharacters) throw new InvalidDataException("PDF expands beyond the secure text limit.");
            }
        }
        finally { try { stream.Position = original; } catch { } }
    }
}
