using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public partial class SqlStorage
    {
        public sealed class EmbeddingRow
        {
            public int DocumentId { get; set; }
            public string Url { get; set; } = string.Empty;
            public string Text { get; set; } = string.Empty;
            public float[] Embedding { get; set; } = Array.Empty<float>();
            public DateTime CreatedAt { get; set; }
        }

        // Return up to `max` embedding rows (most recent first). Best-effort, avoids loading huge sets.
        public async Task<List<EmbeddingRow>> GetEmbeddingRowsAsync(int max = 10000)
        {
            var outList = new List<EmbeddingRow>();
            try
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync().ConfigureAwait(false);

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT TOP (@max) d.id AS DocumentId, d.url, d.text, e.embedding, e.created_at
FROM dbo.Embeddings e
JOIN dbo.documents d ON e.document_id = d.id
ORDER BY e.created_at DESC;
";
                cmd.Parameters.AddWithValue("@max", max);

                using var rdr = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess).ConfigureAwait(false);
                while (await rdr.ReadAsync().ConfigureAwait(false))
                {
                    try
                    {
                        var docId = rdr.GetInt32(0);
                        var url = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1);
                        var text = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2);

                        float[] emb = Array.Empty<float>();
                        if (!rdr.IsDBNull(3))
                        {
                            var bytes = (byte[])rdr.GetValue(3);
                            if (bytes != null && bytes.Length >= 4)
                            {
                                var count = bytes.Length / 4;
                                emb = new float[count];
                                Buffer.BlockCopy(bytes, 0, emb, 0, bytes.Length);
                            }
                        }

                        var created = rdr.IsDBNull(4) ? DateTime.MinValue : rdr.GetDateTime(4);

                        outList.Add(new EmbeddingRow
                        {
                            DocumentId = docId,
                            Url = url,
                            Text = text,
                            Embedding = emb,
                            CreatedAt = created
                        });
                    }
                    catch
                    {
                        // Skip malformed row but continue
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read embeddings from DB");
            }

            return outList;
        }
    }
}