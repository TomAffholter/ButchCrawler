-- 006_AddDocumentChunks.sql
-- Add document_chunks table and link Embeddings to chunks (idempotent)

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'dbo' AND t.name = 'document_chunks')
BEGIN
	CREATE TABLE dbo.document_chunks (
		id BIGINT IDENTITY PRIMARY KEY,
		document_id BIGINT NOT NULL,
		chunk_index INT NOT NULL,
		chunk_text NVARCHAR(MAX) NOT NULL,
		chunk_chars INT NOT NULL,
		created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
	);
END

-- Add document_chunk_id column to Embeddings table if missing
IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id WHERE t.name = 'Embeddings' AND c.name = 'document_chunk_id')
BEGIN
	ALTER TABLE dbo.Embeddings ADD document_chunk_id BIGINT NULL;
END

-- Optional FK index
IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id WHERE t.name = 'Embeddings' AND i.name = 'IX_Embeddings_DocChunk')
BEGIN
	CREATE INDEX IX_Embeddings_DocChunk ON dbo.Embeddings(document_chunk_id);
END

-- Optional FK constraint if document_chunks exists
IF EXISTS (SELECT 1 FROM sys.tables t WHERE t.name = 'document_chunks') AND EXISTS (SELECT 1 FROM sys.tables t WHERE t.name = 'Embeddings')
BEGIN
	IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID('dbo.Embeddings') AND fk.referenced_object_id = OBJECT_ID('dbo.document_chunks'))
	BEGIN
		-- Add foreign key constraint (nullable parent)
		ALTER TABLE dbo.Embeddings
		ADD CONSTRAINT FK_Embeddings_DocumentChunks FOREIGN KEY (document_chunk_id) REFERENCES dbo.document_chunks(id);
	END
END
