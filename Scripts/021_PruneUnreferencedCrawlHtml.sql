-- Optional maintenance: run on a verified backup copy with all crawlers stopped.
-- Review how many blobs have no surviving archive or snapshot reference before enabling cleanup.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Apply bit = 0;

IF OBJECT_ID(N'dbo.CrawlHtmlBlobs', N'U') IS NULL
    THROW 51197, 'Compressed HTML storage has not been installed.', 1;

SELECT COUNT_BIG(*) AS UnreferencedBlobs, COALESCE(SUM(DATALENGTH(b.CompressedHtml)), 0) AS CompressedBytes
FROM dbo.CrawlHtmlBlobs AS b
WHERE NOT EXISTS (SELECT 1 FROM dbo.CrawlArtifacts AS a WHERE a.RawHtmlBlobId = b.Id)
  AND NOT EXISTS (SELECT 1 FROM dbo.CrawlCaptures AS c WHERE c.RawHtmlBlobId = b.Id);

IF @Apply = 1
BEGIN
    DECLARE @deleted int = 1, @total bigint = 0;
    WHILE @deleted > 0
    BEGIN
        DELETE TOP (250) b FROM dbo.CrawlHtmlBlobs AS b
        WHERE NOT EXISTS (SELECT 1 FROM dbo.CrawlArtifacts AS a WHERE a.RawHtmlBlobId = b.Id)
          AND NOT EXISTS (SELECT 1 FROM dbo.CrawlCaptures AS c WHERE c.RawHtmlBlobId = b.Id);
        SET @deleted = @@ROWCOUNT;
        SET @total += @deleted;
    END;
    SELECT @total AS RemovedUnreferencedBlobs;
END;
