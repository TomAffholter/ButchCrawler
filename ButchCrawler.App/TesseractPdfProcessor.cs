using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App;

public class TesseractPdfProcessor : IPdfProcessor
{
    private readonly IConfiguration _config;
    private readonly ILogger<TesseractPdfProcessor> _logger;
    public TesseractPdfProcessor(ILogger<TesseractPdfProcessor> logger, IConfiguration config)
    { _logger = logger; _config = config; }

    public async Task<(string Text, byte[] ExtractedHash)> ExtractTextAsync(Stream pdfStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "ButchCrawler-PDF-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var maxPdfBytes = Math.Clamp(_config.GetValue<long>("Security:MaxPdfBytes", 100_000_000), 1_000_000, 500_000_000);
            if (pdfStream.CanSeek && pdfStream.Length > maxPdfBytes)
                throw new InvalidDataException("PDF exceeds the secure processing size limit.");
            var maxPages = Math.Clamp(_config.GetValue<int>("Security:MaxPdfPages", 500), 1, 2000);
            var maxTextCharacters = Math.Clamp(_config.GetValue<int>("Security:MaxExtractedCharacters", 50_000_000), 100_000, 100_000_000);
            // Preserve a seekable original for extraction and raster fallback.
            var pdfPath = Path.Combine(directory, "source.pdf");
            await using (var output = File.Create(pdfPath))
            {
                if (pdfStream.CanSeek) pdfStream.Position = 0;
                await pdfStream.CopyToAsync(output, cancellationToken);
            }
            using (var validationInput = File.OpenRead(pdfPath))
                SecurePdfValidator.Validate(validationInput, maxPages, maxTextCharacters);
            List<string>? digitalPages = null;
            try
            {
                using var input = File.OpenRead(pdfPath);
                digitalPages = PdfHelper.ExtractPagesFromPdf(input);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _logger.LogDebug(ex, "Selectable PDF text unavailable; checking pages with OCR"); }

            var minimumPageCharacters = Math.Clamp(_config.GetValue<int>("Ocr:MinPdfPageCharacters", 40), 1, 5000);
            var pageCount = digitalPages?.Count ?? 0;
            var pagesNeedingOcr = digitalPages == null
                ? Array.Empty<int>()
                : digitalPages.Select((text, index) => (text, page: index + 1))
                    .Where(item => !HasUsableText(item.text, minimumPageCharacters)).Select(item => item.page).ToArray();

            Dictionary<int, string> ocrPages = new();
            if (digitalPages == null || pagesNeedingOcr.Length > 0)
            {
                var images = await RenderAsync(pdfPath, directory, maxPages, cancellationToken);
                if (digitalPages == null)
                {
                    pageCount = images.Count;
                    pagesNeedingOcr = Enumerable.Range(1, pageCount).ToArray();
                }
                var ocr = new ImageProcessor(Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageProcessor>.Instance, _config);
                foreach (var pageNumber in pagesNeedingOcr)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pageNumber < 1 || pageNumber > images.Count) continue;
                    var image = images[pageNumber - 1];
                    try
                    {
                        var processed = await ocr.ProcessAsync(image, await File.ReadAllBytesAsync(image, cancellationToken), "image/png", ".png", cancellationToken);
                        ocrPages[pageNumber] = processed.Text;
                    }
                    catch (NoReadableTextException)
                    {
                        _logger.LogInformation("PDF page {Page} contained no readable OCR text", pageNumber);
                        ocrPages[pageNumber] = string.Empty;
                    }
                }
            }

            var finalText = ComposePageText(pageCount, digitalPages, ocrPages, minimumPageCharacters, maxTextCharacters);
            if (string.IsNullOrWhiteSpace(finalText)) throw new InvalidDataException("No readable text was extracted from the PDF.");
            return (finalText, PdfHelper.ComputeSha256(finalText));
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private async Task<List<string>> RenderAsync(string pdf, string directory, int maxPages, CancellationToken ct)
    {
        var candidates = new List<string>();
        if (TrustedExecutable.TryResolve(_config["Ghostscript:Path"], new[] { "gswin64c.exe", "gswin32c.exe", "gs.exe" }, out var ghostscript))
            candidates.Add(ghostscript!);
        if (TrustedExecutable.TryResolve(_config["ImageMagick:Path"], new[] { "magick.exe" }, out var imageMagick))
            candidates.Add(imageMagick!);
        if (candidates.Count == 0) throw new InvalidOperationException("No trusted PDF renderer is configured.");
        Exception? lastError = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var output = Path.Combine(directory, "render-" + i); Directory.CreateDirectory(output);
            var pattern = Path.Combine(output, "page-%06d.png");
            var renderDpi = Math.Clamp(_config.GetValue<int>("Ocr:PdfRenderDpi", 300), 150, 600);
            var start = new ProcessStartInfo(candidates[i]!);
            if (Path.GetFileName(candidates[i]).Equals("magick.exe", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var arg in new[] { "-limit", "memory", "256MiB", "-limit", "map", "512MiB", "-limit", "disk", "1GiB",
                    "-density", renderDpi.ToString(System.Globalization.CultureInfo.InvariantCulture), pdf + "[0-" + (maxPages - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]", pattern }) start.ArgumentList.Add(arg);
            }
            else
            {
                foreach (var arg in new[] { "-dSAFER", "-dNOPAUSE", "-dBATCH", "-dNumRenderingThreads=1",
                    "-dFirstPage=1", "-dLastPage=" + maxPages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-sDEVICE=png16m", "-r" + renderDpi.ToString(System.Globalization.CultureInfo.InvariantCulture), "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", "-sOutputFile=" + pattern, pdf }) start.ArgumentList.Add(arg);
            }
            try
            {
                var timeoutSeconds = Math.Clamp(_config.GetValue<int>("Ocr:PdfRenderTimeoutSeconds", 300), 30, 3600);
                await ExternalProcessRunner.RunAsync(start, TimeSpan.FromSeconds(timeoutSeconds), ct);
                var files = Directory.GetFiles(output, "page-*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
                if (files.Count == 0) throw new InvalidDataException("PDF renderer produced no pages.");
                if (files.Count > maxPages) throw new InvalidDataException("PDF renderer exceeded the secure page limit.");
                return files;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogDebug(ex, "PDF renderer failed: {Renderer}", candidates[i]);
            }
        }
        throw new InvalidDataException("PDF OCR could not render the document. Check Ghostscript or ImageMagick installation and the source PDF.", lastError);
    }

    internal static bool HasUsableText(string? text, int minimumCharacters)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lettersOrDigits = text.Count(char.IsLetterOrDigit);
        return lettersOrDigits >= minimumCharacters;
    }

    internal static string ComposePageText(int pageCount, IReadOnlyList<string>? digitalPages,
        IReadOnlyDictionary<int, string> ocrPages, int minimumCharacters, int maxCharacters)
    {
        var result = new StringBuilder();
        for (var page = 1; page <= pageCount; page++)
        {
            var digital = digitalPages != null && page <= digitalPages.Count ? digitalPages[page - 1] : string.Empty;
            var selected = HasUsableText(digital, minimumCharacters) ? digital :
                ocrPages.TryGetValue(page, out var recognized) ? recognized : string.Empty;
            if (string.IsNullOrWhiteSpace(selected)) continue;
            if (result.Length > 0) result.AppendLine().AppendLine();
            result.Append("[Page ").Append(page).AppendLine("]");
            result.Append(selected.Trim());
            if (result.Length > maxCharacters) throw new InvalidDataException("PDF extraction expands beyond the secure text limit.");
        }
        return result.ToString().Trim();
    }
}
