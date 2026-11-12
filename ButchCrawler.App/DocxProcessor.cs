using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ButchCrawler.App
{
    public class DocxProcessor : IPayloadProcessor
    {
        private static readonly Regex TagRegex = new("<.*?>", RegexOptions.Compiled | RegexOptions.Singleline);

        public Task<ProcessedPayload> ProcessAsync(string url, byte[] body, string? contentType, string? extension, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (body.LongLength > 50_000_000) throw new InvalidDataException("The Word document exceeds the secure 50 MB processing limit.");
            ValidatePackage(body);

            // Compute content hash
            byte[] hash;
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(body ?? Array.Empty<byte>());
            }

            string text = string.Empty;
            string? title = null;
            string? author = null;

            // Try robust Open XML extraction first
            try
            {
                using var ms = new MemoryStream(body ?? Array.Empty<byte>());
                using var wordDoc = WordprocessingDocument.Open(ms, false);

                // Title / Author from package properties
                try
                {
                    title = wordDoc.PackageProperties?.Title;
                    author = wordDoc.PackageProperties?.Creator;
                }
                catch { }

                var sb = new StringBuilder();

                var main = wordDoc.MainDocumentPart;
                if (main?.Document?.Body != null)
                {
                    var docBody = main.Document.Body;

                    // Paragraphs
                    foreach (var para in docBody.Elements<Paragraph>())
                    {
                        if (ct.IsCancellationRequested) break;
                        var paraText = string.Join(string.Empty, para.Descendants<Text>().Select(t => t.Text));
                        if (!string.IsNullOrWhiteSpace(paraText)) sb.AppendLine(paraText);
                    }

                    // Tables: include rows/cells with tabs
                    foreach (var table in docBody.Descendants<Table>())
                    {
                        if (ct.IsCancellationRequested) break;
                        foreach (var row in table.Elements<TableRow>())
                        {
                            var cells = row.Elements<TableCell>().Select(cell => string.Join(" ", cell.Descendants<Text>().Select(t => t.Text))).ToArray();
                            sb.AppendLine(string.Join('\t', cells));
                        }
                    }
                }

                // Headers
                try
                {
                    if (main != null)
                    {
                        foreach (var headerPart in main.HeaderParts)
                        {
                            if (ct.IsCancellationRequested) break;
                            var header = headerPart.Header;
                            if (header == null) continue;
                            foreach (var para in header.Elements<Paragraph>())
                            {
                                var hText = string.Join(string.Empty, para.Descendants<Text>().Select(t => t.Text));
                                if (!string.IsNullOrWhiteSpace(hText)) sb.AppendLine(hText);
                            }
                        }

                        // Footers
                        foreach (var footerPart in main.FooterParts)
                        {
                            if (ct.IsCancellationRequested) break;
                            var footer = footerPart.Footer;
                            if (footer == null) continue;
                            foreach (var para in footer.Elements<Paragraph>())
                            {
                                var fText = string.Join(string.Empty, para.Descendants<Text>().Select(t => t.Text));
                                if (!string.IsNullOrWhiteSpace(fText)) sb.AppendLine(fText);
                            }
                        }
                    }
                }
                catch { /* ignore header/footer extraction errors */ }

                text = sb.ToString().Trim();
            }
            catch (Exception)
            {
                // Fallback: try tolerant ZIP-based parsing for malformed docx
                try
                {
                    using var ms = new MemoryStream(body ?? Array.Empty<byte>());
                    using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);
                    var sb = new StringBuilder();

                    // document.xml
                    AppendXmlEntryText(zip, "word/document.xml", sb);

                    // headers and footers
                    foreach (var e in zip.Entries)
                    {
                        if (ct.IsCancellationRequested) break;
                        var name = e.FullName.Replace('\\', '/');
                        if (name.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase))
                        {
                            using var s = e.Open();
                            using var sr = new StreamReader(s, Encoding.UTF8);
                            var xml = sr.ReadToEnd();
                            sb.AppendLine(StripTags(xml));
                        }
                    }

                    // Try core properties for title/creator
                    try
                    {
                        var coreEntry = zip.GetEntry("docProps/core.xml");
                        if (coreEntry != null)
                        {
                            using var s = coreEntry.Open();
                            using var sr = new StreamReader(s, Encoding.UTF8);
                            var coreXml = sr.ReadToEnd();
                            title = ExtractXmlTag(coreXml, "{http://purl.org/dc/elements/1.1/}title", "dc:title", "title");
                            author = ExtractXmlTag(coreXml, "{http://purl.org/dc/elements/1.1/}creator", "dc:creator", "creator");
                        }
                    }
                    catch { }

                    text = sb.ToString().Trim();
                }
                catch
                {
                    // Give up gracefully
                    text = string.Empty;
                }
            }

            if (text.Length > 50_000_000) throw new InvalidDataException("The Word document expands beyond the secure text limit.");
            var result = new ProcessedPayload
            {
                ContentHash = hash,
                Text = text ?? string.Empty,
                Title = string.IsNullOrWhiteSpace(title) ? null : title,
                Author = string.IsNullOrWhiteSpace(author) ? null : author
            };

            return Task.FromResult(result);
        }

        private static void AppendXmlEntryText(ZipArchive zip, string entryName, StringBuilder sb)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) return;
            using var s = entry.Open();
            using var sr = new StreamReader(s, Encoding.UTF8);
            var xml = sr.ReadToEnd();
            sb.AppendLine(StripTags(xml));
        }

        private static void ValidatePackage(byte[] body)
        {
            using var stream = new MemoryStream(body, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (zip.Entries.Count > 5000) throw new InvalidDataException("The Word document contains too many package entries.");
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > 50_000_000) throw new InvalidDataException("A Word document component exceeds the secure expansion limit.");
                expanded = checked(expanded + entry.Length);
                if (expanded > 100_000_000) throw new InvalidDataException("The Word document expands beyond the secure package limit.");
            }
        }

        private static string StripTags(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return string.Empty;
            var t = TagRegex.Replace(xml, " ");
            return Regex.Replace(t, "\\s+", " ").Trim();
        }

        private static string? ExtractXmlTag(string xml, string fullName, string altName, string plainName)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            // Try multiple tag names
            var m = Regex.Match(xml, $"<({Regex.Escape(altName)}|{Regex.Escape(plainName)}|{Regex.Escape(fullName)})[^>]*>(.*?)</", RegexOptions.Singleline);
            if (m.Success) return StripTags(m.Groups[2].Value);
            // fallback simple search
            m = Regex.Match(xml, $"<[^>]*title[^>]*>(.*?)</", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (m.Success) return StripTags(m.Groups[1].Value);
            return null;
        }
    }
}
