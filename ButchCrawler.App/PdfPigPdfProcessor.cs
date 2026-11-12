using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    public class PdfPigPdfProcessor : IPdfProcessor
    {
        public Task<(string Text, byte[] ExtractedHash)> ExtractTextAsync(Stream pdfStream, CancellationToken ct)
        {
            SecurePdfValidator.Validate(pdfStream, 500, 50_000_000);
            var text = PdfHelper.ExtractTextFromPdf(pdfStream);
            var hash = PdfHelper.ComputeSha256(text);
            return Task.FromResult((text, hash));
        }
    }
}
