using System;
using System.Data;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.App
{
    public partial class SqlStorage
    {
        // Compatible with the fresh-start schema: documents.text is mandatory;
        // chunks and Embeddings are the actual tables (not document_chunks).
        public async Task UpsertDocumentAndInsertEmbeddingAsync(string url, string? chunkText,
            string? content, string? contentHash, int? sourceId, int chunkIndex, float[] embedding)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));
            if (url.Length > 500) throw new InvalidOperationException("documents.url supports a maximum of 500 characters; schema migration required for: " + url);
            if (embedding == null || embedding.Length == 0) throw new ArgumentNullException(nameof(embedding));
            var body = content ?? chunkText ?? string.Empty;
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync().ConfigureAwait(false);
            using var tx = (SqlTransaction)await conn.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                int docId = 0;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"SELECT id FROM dbo.documents WITH (UPDLOCK, HOLDLOCK) WHERE url=@url";
                    cmd.Parameters.Add("@url", SqlDbType.VarChar, 500).Value = url;
                    var current = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (current != null && current != DBNull.Value)
                    {
                        docId = Convert.ToInt32(current);
                        cmd.Parameters.Clear();
                        cmd.CommandText = @"UPDATE dbo.documents SET [text]=@body, content=@unicode, content_hash=@hash, source_id=@source, last_crawled=GETDATE() WHERE id=@id";
                        cmd.Parameters.Add("@id", SqlDbType.Int).Value = docId;
                    }
                    else
                    {
                        cmd.Parameters.Clear();
                        cmd.CommandText = @"INSERT INTO dbo.documents(url,[text],content,created_at,source_id,content_hash,last_crawled)
VALUES(@url,@body,@unicode,GETDATE(),@source,@hash,GETDATE()); SELECT CONVERT(int,SCOPE_IDENTITY());";
                        cmd.Parameters.Add("@url", SqlDbType.VarChar, 500).Value = url;
                    }
                    cmd.Parameters.Add("@body", SqlDbType.VarChar, -1).Value = body;
                    cmd.Parameters.Add("@unicode", SqlDbType.NVarChar, -1).Value = body;
                    cmd.Parameters.Add("@hash", SqlDbType.VarChar, 64).Value = (object?)contentHash ?? DBNull.Value;
                    cmd.Parameters.Add("@source", SqlDbType.Int).Value = (object?)sourceId ?? DBNull.Value;
                    var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (result != null && result != DBNull.Value) docId = Convert.ToInt32(result);
                }
                if (docId == 0) throw new InvalidOperationException("Document insert returned no identity");
                int chunkId = 0;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT id FROM dbo.chunks WITH (UPDLOCK, HOLDLOCK) WHERE document_id=@doc AND chunk_index=@idx";
                    cmd.Parameters.Add("@doc", SqlDbType.Int).Value = docId;
                    cmd.Parameters.Add("@idx", SqlDbType.Int).Value = chunkIndex;
                    var found = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    cmd.Parameters.Clear();
                    if (found != null && found != DBNull.Value)
                    {
                        chunkId = Convert.ToInt32(found);
                        cmd.CommandText = "UPDATE dbo.chunks SET chunk_text=@chunk WHERE id=@id";
                        cmd.Parameters.Add("@id", SqlDbType.Int).Value = chunkId;
                    }
                    else
                    {
                        cmd.CommandText = "INSERT dbo.chunks(document_id,chunk_index,chunk_text,created_at) VALUES(@doc,@idx,@chunk,GETDATE()); SELECT CONVERT(int,SCOPE_IDENTITY())";
                        cmd.Parameters.Add("@doc", SqlDbType.Int).Value = docId;
                        cmd.Parameters.Add("@idx", SqlDbType.Int).Value = chunkIndex;
                    }
                    cmd.Parameters.Add("@chunk", SqlDbType.VarChar, -1).Value = chunkText ?? string.Empty;
                    var inserted = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (inserted != null && inserted != DBNull.Value) chunkId = Convert.ToInt32(inserted);
                }
                // Legacy Embeddings schema has no chunk FK. Store per-chunk vectors as JSON including index.
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT dbo.Embeddings(document_id,embedding,created_at) VALUES(@doc,@embedding,GETDATE())";
                    cmd.Parameters.Add("@doc", SqlDbType.Int).Value = docId;
                    cmd.Parameters.Add("@embedding", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(new { chunkIndex, chunkId, vector = embedding });
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                await tx.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
