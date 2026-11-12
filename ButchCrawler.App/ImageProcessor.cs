using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App;

public sealed class NoReadableTextException(string message) : Exception(message);

public class ImageProcessor : IPayloadProcessor
{
    private readonly IConfiguration _config;
    private readonly ILogger<ImageProcessor> _logger;
    public ImageProcessor(ILogger<ImageProcessor> logger, IConfiguration config) { _logger = logger; _config = config; }

    public async Task<ProcessedPayload> ProcessAsync(string url, byte[] body, string? contentType, string? extension, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "ButchCrawler-OCR-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "output");
            if (body.LongLength > Math.Clamp(_config.GetValue<long>("Security:MaxImageBytes", 25_000_000), 1_000_000, 100_000_000))
                throw new InvalidDataException("Image exceeds the secure OCR size limit.");
            var validatedExtension = string.IsNullOrWhiteSpace(extension) ? contentType?.ToLowerInvariant() switch
            { "image/jpeg" or "image/jpg" => ".jpg", "image/png" => ".png", "image/bmp" => ".bmp", "image/tiff" => ".tiff", "image/webp" => ".webp", _ => extension } : extension;
            var maxPixels = Math.Clamp(_config.GetValue<long>("Security:MaxImagePixels", 50_000_000), 1_000_000, 250_000_000);
            var input = Path.Combine(directory, "input" + validatedExtension);
            await File.WriteAllBytesAsync(input, body, ct);
            var safeInput = input;
            try { ImageSecurityValidator.Validate(body, validatedExtension, maxPixels); }
            catch (InvalidDataException ex) when (ex.Message.Contains("pixel processing limit", StringComparison.Ordinal))
            {
                safeInput = await DownscaleOversizedImageAsync(input, validatedExtension, directory, maxPixels, ct);
            }
            var ocrInput = await PrepareForOcrAsync(safeInput, Path.GetExtension(safeInput), directory, ct);
            var executable = TrustedExecutable.Resolve(_config["Tesseract:Path"], "tesseract.exe");
            var start = new ProcessStartInfo(executable);
            foreach (var arg in new[] { ocrInput, output, "-l", _config["Tesseract:Language"] ?? "eng",
                "--oem", Math.Clamp(_config.GetValue<int>("Ocr:EngineMode", 1), 0, 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--psm", Math.Clamp(_config.GetValue<int>("Ocr:PageSegmentationMode", 3), 0, 13).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--dpi", Math.Clamp(_config.GetValue<int>("Ocr:InputDpi", 300), 70, 1200).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-c", "preserve_interword_spaces=1" }) start.ArgumentList.Add(arg);
            if (!string.IsNullOrWhiteSpace(_config["Tesseract:TessdataPath"]))
            {
                start.ArgumentList.Add("--tessdata-dir"); start.ArgumentList.Add(_config["Tesseract:TessdataPath"]!);
            }
            await ExternalProcessRunner.RunAsync(start, TimeSpan.FromMilliseconds(Math.Max(1, _config.GetValue<int>("Ocr:TimeoutMs", 60000))), ct);
            var text = await File.ReadAllTextAsync(output + ".txt", ct);
            if (string.IsNullOrWhiteSpace(text)) throw new NoReadableTextException("OCR found no readable text in this image.");
            return new ProcessedPayload { Text = text, ContentHash = SHA256.HashData(body) };
        }
        finally { try { Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private async Task<string> PrepareForOcrAsync(string input, string? extension, string directory, CancellationToken ct)
    {
        var mustConvert = extension == ".webp";
        if ((!_config.GetValue<bool>("Ocr:PreprocessImages", true) && !mustConvert) || extension is ".tif" or ".tiff")
            return input;
        if (!TrustedExecutable.TryResolve(_config["ImageMagick:Path"], new[] { "magick.exe" }, out var imageMagick))
        {
            if (mustConvert) throw new InvalidOperationException("ImageMagick is required to OCR WebP images.");
            return input;
        }

        var prepared = Path.Combine(directory, "prepared.png");
        var start = new ProcessStartInfo(imageMagick!);
        foreach (var arg in new[]
        {
            "-limit", "memory", "512MiB", "-limit", "map", "1GiB", "-limit", "disk", "2GiB",
            input, "-auto-orient", "-colorspace", "Gray", "-deskew", "40%", "-contrast-stretch", "0x1%", prepared
        }) start.ArgumentList.Add(arg);
        try
        {
            var timeout = TimeSpan.FromMilliseconds(Math.Max(1, _config.GetValue<int>("Ocr:PreprocessTimeoutMs", 60000)));
            await ExternalProcessRunner.RunAsync(start, timeout, ct);
            if (File.Exists(prepared) && new FileInfo(prepared).Length > 0) return prepared;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogDebug(ex, "Image preprocessing failed; using the validated original image"); }
        return input;
    }

    private async Task<string> DownscaleOversizedImageAsync(string input, string? extension, string directory, long maxPixels, CancellationToken ct)
    {
        if (extension is ".tif" or ".tiff" ||
            !TrustedExecutable.TryResolve(_config["ImageMagick:Path"], new[] { "magick.exe" }, out var imageMagick))
            throw new InvalidDataException($"Image exceeds the secure {maxPixels:N0}-pixel processing limit. Install or configure ImageMagick to safely reduce large scans.");
        var workingPixels = Math.Clamp(_config.GetValue<long>("Ocr:MaxWorkingImagePixels", 20_000_000), 1_000_000, maxPixels);
        var side = Math.Max(1000, (int)Math.Floor(Math.Sqrt(workingPixels)));
        var resized = Path.Combine(directory, "downscaled.png");
        var start = new ProcessStartInfo(imageMagick!);
        foreach (var arg in new[] { "-limit", "memory", "512MiB", "-limit", "map", "1GiB", "-limit", "disk", "2GiB" })
            start.ArgumentList.Add(arg);
        if (extension is ".jpg" or ".jpeg")
        {
            // Ask the JPEG decoder to subsample before allocating the full source bitmap.
            start.ArgumentList.Add("-define"); start.ArgumentList.Add($"jpeg:size={side}x{side}");
        }
        foreach (var arg in new[] { input, "-auto-orient", "-resize", $"{side}x{side}>", resized })
            start.ArgumentList.Add(arg);
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, _config.GetValue<int>("Ocr:PreprocessTimeoutMs", 60000)));
        await ExternalProcessRunner.RunAsync(start, timeout, ct);
        var resizedBytes = await File.ReadAllBytesAsync(resized, ct);
        ImageSecurityValidator.Validate(resizedBytes, ".png", maxPixels);
        _logger.LogInformation("Safely reduced an oversized scan to about {WorkingPixels:N0} pixels for OCR", workingPixels);
        return resized;
    }
}
