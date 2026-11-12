using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    public interface IPdfProcessor
    {
        Task<(string Text, byte[] ExtractedHash)> ExtractTextAsync(Stream pdfStream, CancellationToken ct);
    }
}