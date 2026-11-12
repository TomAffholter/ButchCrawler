using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public class EmbeddingIndexService
    {
        private readonly EmbeddingService _embed;
        private readonly SqlStorage _storage;
        private readonly ILogger<EmbeddingIndexService> _logger;

        public EmbeddingIndexService(EmbeddingService embed, SqlStorage storage, ILogger<EmbeddingIndexService> logger)
        {
            _embed = embed ?? throw new ArgumentNullException(nameof(embed));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        // Index a document (generate embedding and persist)
        public async Task IndexDocumentAsync(string url, string? titleOrText, string? content, string? contentHash = null, int? sourceId = null)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));
            // Prefer full body content; fall back to title/text only when body is unavailable.
            var textToEmbed = !string.IsNullOrWhiteSpace(content) ? content : titleOrText;
            if (string.IsNullOrWhiteSpace(textToEmbed))
            {
                _logger.LogDebug("Skipping indexing for {Url} because no text available", url);
                return;
            }

            // If the text is large, split into chunks to avoid API rejections and to create finer-grained embeddings.
            try
            {
                // Sanitize input (strip HTML, decode entities) and use conservative character-based chunks.
                // Many pages contain HTML which greatly increases tokenization; strip tags first.
                var raw = textToEmbed;
                try
                {
                    // remove HTML tags
                    raw = System.Text.RegularExpressions.Regex.Replace(raw, "<.*?>", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
                    // decode HTML entities
                    raw = System.Net.WebUtility.HtmlDecode(raw);
                    // collapse whitespace
                    raw = System.Text.RegularExpressions.Regex.Replace(raw, "\\s+", " ").Trim();
                }
                catch { }

                // Conservative chunk size in characters. Approx tokens ~= chars/4; set to ~2000 chars (~500 tokens)
                var chunks = EmbeddingIndexServiceHelpers.SplitIntoChunksByChars(raw, maxChars: 2000, overlapChars: 200);
                _logger.LogDebug("Prepared {Chunks} chunks for {Url} (cleanChars={Len})", chunks.Count, url, raw.Length);
                if (chunks.Count == 0)
                {
                    _logger.LogDebug("No chunks produced for {Url}; skipping indexing", url);
                    return;
                }

                var chunkIndex = 0;
                foreach (var chunk in chunks)
                {
                    chunkIndex++;
                    try
                    {
                        // skip empty chunks just in case
                        if (string.IsNullOrWhiteSpace(chunk))
                            continue;

                        var chunkForEmbedding = !string.IsNullOrWhiteSpace(titleOrText)
                            ? $"Title: {titleOrText}\n\n{chunk}"
                            : chunk;

                        _logger.LogDebug("Generating embedding for {Url} chunk {Index}/{Total} (chars={Len})", url, chunkIndex, chunks.Count, chunkForEmbedding.Length);
                        var emb = await _embed.GenerateEmbeddingAsync(chunkForEmbedding).ConfigureAwait(false);

                        await _storage.UpsertDocumentAndInsertEmbeddingAsync(url, chunkForEmbedding, content, contentHash, sourceId, chunkIndex, emb).ConfigureAwait(false);

                        _logger.LogDebug("Indexed embedding for {Url} chunk {Index} (dim={Dim})", url, chunkIndex, emb.Length);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to generate/store embedding for {Url} chunk {Index}", url, chunkIndex);
                        // continue with next chunk rather than aborting the entire document
                    }
                }

            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate or store embedding for {Url}", url);
                throw;
            }
        }
    }
}

// Utility helpers for embedding indexing
namespace ButchCrawler.App
{
    public static class EmbeddingIndexServiceHelpers
    {
        // Split text into character chunks with overlap. Returns list of chunks.
        public static List<string> SplitIntoChunksByChars(string text, int maxChars = 28000, int overlapChars = 500)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            var pos = 0;
            var total = text.Length;
            while (pos < total)
            {
                var len = Math.Min(maxChars, total - pos);
                // Try to avoid cutting in middle of a word: expand backwards to last whitespace if possible
                var chunk = text.Substring(pos, len);
                if (pos + len < total)
                {
                    var lastSpace = chunk.LastIndexOfAny(new char[] { ' ', '\n', '\r', '\t', '.', '?' , '!' });
                    if (lastSpace > Math.Max(len / 2, 20))
                    {
                        chunk = chunk.Substring(0, lastSpace + 1);
                        len = chunk.Length;
                    }
                }

                result.Add(chunk.Trim());

                if (pos + len >= total) break;
                pos = Math.Max(0, pos + len - overlapChars);
            }

            return result;
        }
    }
}
