using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public sealed class EmbeddingSearchService
    {
        private readonly EmbeddingService _embed;
        private readonly SqlStorage _storage;
        private readonly ILogger<EmbeddingSearchService> _logger;

        public EmbeddingSearchService(EmbeddingService embed, SqlStorage storage, ILogger<EmbeddingSearchService> logger)
        {
            _embed = embed ?? throw new ArgumentNullException(nameof(embed));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public sealed record SearchResult(int DocumentId, string Url, string Text, float Score);

        // Query the persisted embeddings in-process. Fetches up to maxCandidates embeddings then ranks by cosine similarity.
        public async Task<List<SearchResult>> QueryAsync(string query, int topK = 10, int maxCandidates = 5000)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<SearchResult>();

            float[] qEmb;
            try
            {
                qEmb = await _embed.GenerateEmbeddingAsync(query).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate query embedding");
                return new List<SearchResult>();
            }

            var rows = await _storage.GetEmbeddingRowsAsync(maxCandidates).ConfigureAwait(false);
            if (rows == null || rows.Count == 0)
            {
                _logger.LogDebug("No embeddings available for search");
                return new List<SearchResult>();
            }

            var qNorm = Norm(qEmb);
            if (qNorm <= 0) return new List<SearchResult>();

            var scored = new List<SearchResult>(Math.Min(topK, 64));
            foreach (var r in rows)
            {
                if (r.Embedding == null || r.Embedding.Length == 0) continue;
                if (r.Embedding.Length != qEmb.Length) continue; // skip dimension mismatch

                var score = CosineSimilarity(qEmb, qNorm, r.Embedding);
                scored.Add(new SearchResult(r.DocumentId, r.Url, r.Text, score));
            }

            var top = scored.OrderByDescending(s => s.Score).Take(topK).ToList();
            _logger.LogDebug("Query returned {Count} results (topK={TopK})", top.Count, topK);
            return top;
        }

        private static float Norm(float[] v)
        {
            double s = 0;
            for (int i = 0; i < v.Length; i++) s += v[i] * v[i];
            return (float)Math.Sqrt(s);
        }

        private static float CosineSimilarity(float[] q, float qNorm, float[] v)
        {
            double dot = 0;
            double vnorm2 = 0;
            for (int i = 0; i < q.Length; i++)
            {
                dot += q[i] * v[i];
                vnorm2 += v[i] * v[i];
            }

            var vnorm = Math.Sqrt(vnorm2);
            if (qNorm <= 0 || vnorm <= 0) return 0f;
            return (float)(dot / (qNorm * vnorm));
        }
    }
}