using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace ButchCrawler.App
{
    public static class PdfHelper
    {
        // Compute SHA256 hash of stream (will NOT rewind stream)
        public static async Task<byte[]> ComputeSha256Async(Stream s, CancellationToken ct)
        {
            using var sha = SHA256.Create();
            if (!s.CanSeek)
            {
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, 81920, ct).ConfigureAwait(false);
                ms.Position = 0;
                return sha.ComputeHash(ms);
            }

            var cur = s.Position;
            s.Position = 0;
            var hash = sha.ComputeHash(s);
            s.Position = cur;
            return hash;
        }

        // Extract text from a PDF stream using PdfPig (stream must be readable/seekable)
        public static string ExtractTextFromPdf(Stream pdfStream)
        {
            if (pdfStream == null) throw new ArgumentNullException(nameof(pdfStream));

            // If the incoming stream is not seekable, copy to a MemoryStream and dispose it after use.
            if (!pdfStream.CanSeek)
            {
                using var ms = new MemoryStream();
                pdfStream.CopyTo(ms);
                ms.Position = 0;
                using var doc = PdfDocument.Open(ms);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                {
                    var txt = page.Text;
                    if (!string.IsNullOrEmpty(txt)) sb.AppendLine(txt);
                }

                return sb.ToString();
            }

            // For seekable streams (e.g. FileStream) do not dispose the original stream — PdfPig will read it but we must not close it here.
            var originalPosition = pdfStream.CanSeek ? pdfStream.Position : 0L;
            try
            {
                pdfStream.Position = 0;
                using var doc = PdfDocument.Open(pdfStream);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                {
                    var txt = page.Text;
                    if (!string.IsNullOrEmpty(txt)) sb.AppendLine(txt);
                }

                return sb.ToString();
            }
            finally
            {
                // restore position if possible
                if (pdfStream.CanSeek)
                {
                    try { pdfStream.Position = originalPosition; } catch { }
                }
            }
        }

        // Extract per-page text into a list so callers can operate on a page at a time
        public static List<string> ExtractPagesFromPdf(Stream pdfStream)
        {
            if (pdfStream == null) throw new ArgumentNullException(nameof(pdfStream));

            var pages = new List<string>();

            // If the incoming stream is not seekable, copy to a MemoryStream and dispose it after use.
            if (!pdfStream.CanSeek)
            {
                using var ms = new MemoryStream();
                pdfStream.CopyTo(ms);
                ms.Position = 0;
                using var doc = PdfDocument.Open(ms);
                foreach (var page in doc.GetPages())
                {
                    var txt = page.Text ?? string.Empty;
                    pages.Add(txt);
                }

                return pages;
            }

            var originalPosition = pdfStream.CanSeek ? pdfStream.Position : 0L;
            try
            {
                pdfStream.Position = 0;
                using var doc = PdfDocument.Open(pdfStream);
                foreach (var page in doc.GetPages())
                {
                    var txt = page.Text ?? string.Empty;
                    pages.Add(txt);
                }

                return pages;
            }
            finally
            {
                if (pdfStream.CanSeek)
                {
                    try { pdfStream.Position = originalPosition; } catch { }
                }
            }
        }

        public static byte[] ComputeSha256(string text)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }
    }
}