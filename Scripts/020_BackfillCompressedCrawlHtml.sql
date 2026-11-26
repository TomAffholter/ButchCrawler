-- Run 019 first on a restored copy with enough room for temporary data and log growth.
-- Set the expected database name below and select that database in the query editor.
-- Stop all crawlers using the copy. This script can be rerun after an interruption.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET CURSOR_CLOSE_ON_COMMIT OFF;

DECLARE @ExpectedCopy sysname = N'ButchCrawlerCompressionTest';
IF DB_NAME() <> @ExpectedCopy OR DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb', N'ButchCrawlerDB')
    THROW 51193, 'Select the restored copy and set @ExpectedCopy to its name. The working ButchCrawlerDB is protected.', 1;
IF OBJECT_ID(N'dbo.StoreCrawlHtmlBlob', N'P') IS NULL
    THROW 51194, 'Apply script 019 on the restored copy first.', 1;
IF @@TRANCOUNT <> 0
    THROW 51195, 'Run this script without an open transaction.', 1;

DECLARE @kind tinyint, @url nvarchar(2000), @captureId bigint, @html nvarchar(max), @blobId bigint;
DECLARE @processed bigint = 0, @batch int = 0;

-- Only lightweight keys go into the static cursor. Original bodies are read one at a time.
DECLARE legacy CURSOR LOCAL STATIC READ_ONLY FOR
    SELECT CONVERT(tinyint, 0), Url, CONVERT(bigint, NULL)
    FROM dbo.CrawlArtifacts WHERE RawHtml IS NOT NULL
    UNION ALL
    SELECT CONVERT(tinyint, 1), CONVERT(nvarchar(2000), NULL), Id
    FROM dbo.CrawlCaptures WHERE CaptureType = N'html' AND BodyTextFull IS NOT NULL;

BEGIN TRY
    OPEN legacy;
    FETCH NEXT FROM legacy INTO @kind, @url, @captureId;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF @batch = 0 BEGIN TRANSACTION;
        SET @html = NULL;
        IF @kind = 0
            SELECT @html = RawHtml FROM dbo.CrawlArtifacts WITH (UPDLOCK) WHERE Url = @url;
        ELSE
            SELECT @html = BodyTextFull FROM dbo.CrawlCaptures WITH (UPDLOCK)
                WHERE Id = @captureId AND CaptureType = N'html';

        IF @html IS NOT NULL
        BEGIN
            EXEC dbo.StoreCrawlHtmlBlob @Html = @html, @BlobId = @blobId OUTPUT;

            -- Confirm exact bytes before clearing the old field, including Unicode and whitespace.
            DECLARE @original varbinary(max) = CONVERT(varbinary(max), @html);
            IF NOT EXISTS
            (
                SELECT 1 FROM dbo.CrawlHtmlBlobs
                WHERE Id = @blobId AND OriginalBytes = DATALENGTH(@original)
                  AND DECOMPRESS(CompressedHtml) = @original
            )
                THROW 51196, 'HTML verification failed. This batch was rolled back.', 1;

            IF @kind = 0
                UPDATE dbo.CrawlArtifacts SET RawHtmlBlobId = @blobId, RawHtml = NULL WHERE Url = @url;
            ELSE
                UPDATE dbo.CrawlCaptures SET RawHtmlBlobId = @blobId, BodyTextFull = NULL
                    WHERE Id = @captureId AND CaptureType = N'html';
            SET @processed += 1;
        END;

        SET @batch += 1;
        IF @batch = 25
        BEGIN
            COMMIT TRANSACTION;
            SET @batch = 0;
        END;
        FETCH NEXT FROM legacy INTO @kind, @url, @captureId;
    END;

    IF @batch > 0 COMMIT TRANSACTION;
    CLOSE legacy;
    DEALLOCATE legacy;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT @processed AS ConvertedReferences,
       (SELECT COUNT_BIG(*) FROM dbo.CrawlArtifacts WHERE RawHtml IS NOT NULL)
       + (SELECT COUNT_BIG(*) FROM dbo.CrawlCaptures WHERE CaptureType = N'html' AND BodyTextFull IS NOT NULL)
       AS RemainingLegacyHtmlReferences;
