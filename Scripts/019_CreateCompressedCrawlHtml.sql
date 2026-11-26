-- Apply to a backed-up crawler database with the crawler stopped.
-- This provisions compressed storage for future writes. It does not rewrite existing data.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.CrawlArtifacts', N'U') IS NULL OR OBJECT_ID(N'dbo.CrawlCaptures', N'U') IS NULL
    THROW 51190, 'Open the crawler database with the crawl artifact and capture tables before running this migration.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.CrawlHtmlBlobs', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CrawlHtmlBlobs
        (
            Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrawlHtmlBlobs PRIMARY KEY,
            ContentHash binary(32) NOT NULL CONSTRAINT UQ_CrawlHtmlBlobs_ContentHash UNIQUE,
            OriginalBytes bigint NOT NULL,
            CompressedHtml varbinary(max) NOT NULL
        );
    END;

    IF COL_LENGTH(N'dbo.CrawlArtifacts', N'RawHtmlBlobId') IS NULL
        ALTER TABLE dbo.CrawlArtifacts ADD RawHtmlBlobId bigint NULL
            CONSTRAINT FK_CrawlArtifacts_RawHtmlBlob REFERENCES dbo.CrawlHtmlBlobs(Id);

    IF COL_LENGTH(N'dbo.CrawlCaptures', N'RawHtmlBlobId') IS NULL
        ALTER TABLE dbo.CrawlCaptures ADD RawHtmlBlobId bigint NULL
            CONSTRAINT FK_CrawlCaptures_RawHtmlBlob REFERENCES dbo.CrawlHtmlBlobs(Id);

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlArtifacts') AND name = N'IX_CrawlArtifacts_RawHtmlBlobId')
        EXEC(N'CREATE INDEX IX_CrawlArtifacts_RawHtmlBlobId ON dbo.CrawlArtifacts(RawHtmlBlobId);');

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlCaptures') AND name = N'IX_CrawlCaptures_RawHtmlBlobId')
        EXEC(N'CREATE INDEX IX_CrawlCaptures_RawHtmlBlobId ON dbo.CrawlCaptures(RawHtmlBlobId);');

    EXEC(N'
CREATE OR ALTER PROCEDURE dbo.StoreCrawlHtmlBlob
    @Html nvarchar(max),
    @BlobId bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    IF @@TRANCOUNT = 0
        THROW 51191, ''StoreCrawlHtmlBlob requires the transaction that saves its owning record.'', 1;
    SET @BlobId = NULL;
    IF @Html IS NULL RETURN;

    DECLARE @bytes varbinary(max) = CONVERT(varbinary(max), @Html);
    DECLARE @length bigint = DATALENGTH(@bytes);
    DECLARE @hash binary(32) = HASHBYTES(''SHA2_256'', @bytes);
    DECLARE @existingLength bigint;

    -- The key-range lock serializes inserts of the same body by parallel workers.
    SELECT @BlobId = Id, @existingLength = OriginalBytes
    FROM dbo.CrawlHtmlBlobs WITH (UPDLOCK, HOLDLOCK)
    WHERE ContentHash = @hash;

    IF @BlobId IS NULL
    BEGIN
        INSERT dbo.CrawlHtmlBlobs (ContentHash, OriginalBytes, CompressedHtml)
        VALUES (@hash, @length, COMPRESS(@bytes));
        SET @BlobId = CONVERT(bigint, SCOPE_IDENTITY());
    END
    ELSE IF @existingLength <> @length
        THROW 51192, ''HTML fingerprint matched a body of a different size.'', 1;
END;');

    EXEC(N'
CREATE OR ALTER VIEW dbo.CrawlArtifactHtml AS
SELECT a.Url, COALESCE(a.RawHtml, CONVERT(nvarchar(max), DECOMPRESS(b.CompressedHtml))) AS RawHtml
FROM dbo.CrawlArtifacts AS a
LEFT JOIN dbo.CrawlHtmlBlobs AS b ON b.Id = a.RawHtmlBlobId;');

    EXEC(N'
CREATE OR ALTER VIEW dbo.CrawlCaptureHtml AS
SELECT c.Id, c.Url, c.RetrievedAt,
       COALESCE(c.BodyTextFull, CONVERT(nvarchar(max), DECOMPRESS(b.CompressedHtml))) AS RawHtml
FROM dbo.CrawlCaptures AS c
LEFT JOIN dbo.CrawlHtmlBlobs AS b ON b.Id = c.RawHtmlBlobId
WHERE c.CaptureType = N''html'';');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
