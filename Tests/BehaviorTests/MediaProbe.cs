using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ButchCrawler.App;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class MediaProbe
{
    public static async Task RunAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Media probe file was not found.", fullPath);
        var config = new ConfigurationBuilder().SetBasePath(Path.GetFullPath("ButchCrawler.App"))
            .AddJsonFile("appsettings.json").Build();
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        string text;
        if (extension == ".pdf")
        {
            await using var input = File.OpenRead(fullPath);
            (text, _) = await new TesseractPdfProcessor(NullLogger<TesseractPdfProcessor>.Instance, config)
                .ExtractTextAsync(input, CancellationToken.None);
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(fullPath);
            text = (await new ImageProcessor(NullLogger<ImageProcessor>.Instance, config)
                .ProcessAsync(fullPath, bytes, null, extension, CancellationToken.None)).Text;
        }
        Console.WriteLine($"PASS: {Path.GetFileName(fullPath)} produced {text.Length:N0} searchable characters");
        Console.WriteLine(text[..Math.Min(text.Length, 500)].Replace('\r', ' ').Replace('\n', ' '));
    }
}
