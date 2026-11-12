using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App;

public class LocalFileCrawler : BackgroundService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp" };
    private readonly PayloadProcessorRegistry _registry;
    private readonly SqlStorage _storage;
    private readonly IEmbeddingIndexQueue _indexQueue;
    private readonly IPdfProcessor _pdfProcessor;
    private readonly ILogger<LocalFileCrawler> _logger;
    private readonly string _rootPath;
    private readonly HashSet<string> _extensions;
    private readonly long _maxBytes;
    private readonly long _maxStructuredBytes;
    private readonly int _maxConcurrency;
    private readonly bool _embeddingEnabled, _audioEnabled;
    private readonly bool _tesseractAvailable, _pdfRendererAvailable;

    public LocalFileCrawler(PayloadProcessorRegistry registry, SqlStorage storage, IEmbeddingIndexQueue indexQueue,
        IPdfProcessor pdfProcessor, IConfiguration config, ILogger<LocalFileCrawler> logger)
    {
        _registry = registry; _storage = storage; _indexQueue = indexQueue; _pdfProcessor = pdfProcessor; _logger = logger;
        _rootPath = config["LocalCrawler:RootPath"] ?? throw new InvalidOperationException("LocalCrawler:RootPath is required.");
        _extensions = new((config.GetSection("LocalCrawler:Extensions").Get<string[]>() ??
            new[] { ".pdf", ".docx", ".txt", ".md", ".html", ".htm", ".mht", ".mhtml", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp", ".csv", ".tsv", ".tab", ".json", ".jsonl", ".ndjson", ".geojson", ".xml", ".sql", ".xlsx", ".xls", ".sqlite", ".sqlite3", ".db", ".mdb", ".accdb", ".dbf", ".zip" })
            .Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        _audioEnabled = config.GetValue<bool>("Audio:Enabled", false);
        if (_audioEnabled) foreach (var ext in AudioProcessor.Extensions) _extensions.Add(ext);
        _embeddingEnabled = config.GetValue<bool>("Indexer:Enabled", false);
        _tesseractAvailable = TrustedExecutable.TryResolve(config["Tesseract:Path"], new[] { "tesseract.exe" }, out _);
        _pdfRendererAvailable = TrustedExecutable.TryResolve(config["Ghostscript:Path"], new[] { "gswin64c.exe", "gswin32c.exe", "gs.exe" }, out _) ||
            TrustedExecutable.TryResolve(config["ImageMagick:Path"], new[] { "magick.exe" }, out _);
        _maxBytes = config.GetValue<long>("LocalCrawler:MaxBytes", 500_000_000);
        _maxStructuredBytes = config.GetValue<long>("StructuredData:MaxDownloadBytes", 50_000_000);
        _maxConcurrency = config.GetValue<int>("LocalCrawler:MaxConcurrency", 2);
        if (_maxConcurrency < 1 || _maxConcurrency > 64 || _maxBytes < 1)
            throw new InvalidOperationException("LocalCrawler requires MaxConcurrency from 1 to 64 and a positive MaxBytes.");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunOnceAsync(stoppingToken);

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_rootPath))
        {
            ReportLocalProgress(0, 0);
            Report("ERROR", "Local scan folder does not exist", _rootPath);
            return;
        }
        if (!_tesseractAvailable && _extensions.Overlaps(ImageExtensions.Append(".pdf")))
            Report("SKIPPED", "Tesseract OCR is unavailable; images and scanned PDFs will be skipped", _rootPath);
        else if (!_pdfRendererAvailable && _extensions.Contains(".pdf"))
            Report("SKIPPED", "Ghostscript/ImageMagick is unavailable; scanned PDFs will be skipped", _rootPath);
        var files = EnumerateFiles(ct).ToList();
        ReportLocalProgress(0, files.Count);
        var keywords = await _storage.GetKeywordsAsync(throwOnError: true);
        if (_extensions.Any(e => StructuredDataDetector.SupportedExtensions.Contains(e, StringComparer.OrdinalIgnoreCase)))
            await _storage.EnsureStructuredDataSchemaAsync(ct);
        Report("LOCAL-SCAN", $"Found {files.Count:N0} supported files; {keywords.Count:N0} active keywords", _rootPath);
        var saved = 0; var failed = 0; var skipped = 0; var processed = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = ct }, async (file, token) =>
        {
            try
            {
                if (await ProcessAsync(file, keywords, token)) Interlocked.Increment(ref saved);
                else Interlocked.Increment(ref skipped);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (NoReadableTextException)
            {
                Interlocked.Increment(ref skipped);
                Report("SKIPPED", "No readable text detected", file);
            }
            catch (Exception ex) when (OcrPrerequisiteUnavailable(file))
            {
                Interlocked.Increment(ref skipped);
                _logger.LogDebug(ex, "Local OCR skipped because a configured prerequisite is unavailable: {File}", file);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                Report("ERROR", "Local file: " + ex.Message, file);
                _logger.LogWarning(ex, "Local processing failed for {File}", file);
            }
            finally
            {
                var current = Interlocked.Increment(ref processed);
                ReportLocalProgress(current, files.Count);
            }
        });
        Report("LOCAL-SCAN", $"Finished: {saved:N0} saved, {failed:N0} failed, {skipped:N0} skipped", _rootPath);
    }

    private IEnumerable<string> EnumerateFiles(CancellationToken ct)
    {
        var directories = new Stack<string>(); directories.Push(_rootPath);
        while (directories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            string[] files, children;
            try { files = Directory.GetFiles(directory); children = Directory.GetDirectories(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Report("ERROR", "Cannot scan folder: " + ex.Message, directory); continue; }
            foreach (var file in files) if (_extensions.Contains(Path.GetExtension(file))) yield return file;
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) directories.Push(child);
                    else Report("SKIPPED", "Local folder link (avoids recursive loops)", child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report("ERROR", ex.Message, child); }
            }
        }
    }

    private async Task<bool> ProcessAsync(string file, IReadOnlyList<string> keywords, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0 || info.Length > _maxBytes)
        { Report("SKIPPED", "Local file missing, empty or above MaxBytes", file); return false; }
        var ext = info.Extension;
        if (StructuredDataDetector.SupportedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) && info.Length > _maxStructuredBytes)
        { Report("SKIPPED", $"Structured file is above its {_maxStructuredBytes:N0}-byte safety limit", file); return false; }
        if (AudioProcessor.IsAudio(null, ext) && !_audioEnabled)
        { Report("SKIPPED", "Audio transcription disabled", file); return false; }
        if (ImageExtensions.Contains(ext) && !_tesseractAvailable) return false;
        Report("LOCAL-START", "Reading local file", file);
        string text; byte[] hash; string? title = null; ProcessedPayload? processedPayload = null;
        var processor = _registry.Resolve(null, ext) ?? (new[] { ".txt", ".md", ".html", ".htm" }.Contains(ext, StringComparer.OrdinalIgnoreCase) ? new LocalTextProcessor() : null);
        if (processor != null)
        {
            var bytes = await File.ReadAllBytesAsync(file, ct);
            if (bytes.LongLength > _maxBytes) throw new InvalidDataException("File grew beyond MaxBytes while being read.");
            processedPayload = await processor.ProcessAsync(file, bytes, null, ext, ct);
            text = processedPayload.Text; hash = processedPayload.ContentHash; title = processedPayload.Title;
        }
        else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await using var input = File.OpenRead(file);
            (text, hash) = await _pdfProcessor.ExtractTextAsync(input, ct);
        }
        else { Report("SKIPPED", "No processor registered for " + ext, file); return false; }
        ct.ThrowIfCancellationRequested();
        await _storage.SaveLocalTextAsync(file, text, ct);
        if (!string.IsNullOrWhiteSpace(processedPayload?.StructuredFormat))
        {
            await _storage.SaveStructuredDataProvenanceAsync(new StructuredDataProvenance(
                file, processedPayload.StructuredFormat, processedPayload.StructuredCategory ?? "Structured data", info.Length,
                processedPayload.StructuredRecordCount, processedPayload.StructuredTableCount, processedPayload.StructuredEntryCount,
                processedPayload.StructuredTruncated, processedPayload.StructuredWarning,
                Convert.ToHexString(hash).ToLowerInvariant(), null), ct);
            Report("STRUCTURED", $"{processedPayload.StructuredFormat}; {(processedPayload.StructuredRecordCount ?? 0):N0} records indexed", file);
        }
        foreach (var keyword in keywords)
        {
            var line = CrawlerService.KeywordMatchLine(file, text, keyword);
            if (line != null) Console.WriteLine(line);
        }
        Report("LOCAL-TEXT", $"{text.Length:N0} characters saved", file);
        Console.WriteLine($"[CRAWL-TYPE] PROCESSED | {ProcessedFileType.Label(null, ext, processedPayload?.StructuredFormat)}");
        if (_embeddingEnabled) await _indexQueue.EnqueueAsync(new IndexRequest(file, title, text, Convert.ToHexString(hash)), ct);
        return true;
    }

    private static void Report(string status, string message, string path) =>
        Console.WriteLine($"[CRAWL-URL] {status} | {message.Replace('\r', ' ').Replace('\n', ' ')} | {path.Replace('\r', ' ').Replace('\n', ' ')}");

    private static void ReportLocalProgress(int processed, int total) =>
        Console.WriteLine($"[CRAWL-LOCAL] PROGRESS | {processed} | {total}");

    private bool OcrPrerequisiteUnavailable(string file)
    {
        var extension = Path.GetExtension(file);
        return (ImageExtensions.Contains(extension) && !_tesseractAvailable) ||
            (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) && (!_tesseractAvailable || !_pdfRendererAvailable));
    }

}
