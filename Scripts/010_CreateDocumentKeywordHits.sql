USE ButchCrawlerDB;
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.CrawlKeywordHits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CrawlKeywordHits
    (
        Id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Url NVARCHAR(2000) NOT NULL,
        UrlHash BINARY(32) NOT NULL,
        KeywordId INT NOT NULL,
        MatchCount INT NOT NULL,
        FirstSnippet NVARCHAR(1000) NULL,
        LastMatchedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_CrawlKeywordHits_LastMatched DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_CrawlKeywordHits_Keyword FOREIGN KEY (KeywordId) REFERENCES dbo.SeedKeywords(Id)
    );
    CREATE UNIQUE INDEX UX_CrawlKeywordHits_UrlHash_Keyword ON dbo.CrawlKeywordHits(UrlHash, KeywordId);
END;

IF COL_LENGTH(N'dbo.CrawlKeywordHits', N'UrlHash') IS NULL
BEGIN
    ALTER TABLE dbo.CrawlKeywordHits ADD UrlHash BINARY(32) NULL;
    UPDATE dbo.CrawlKeywordHits SET UrlHash = HASHBYTES('SHA2_256', CONVERT(VARBINARY(MAX), Url)) WHERE UrlHash IS NULL;
    ALTER TABLE dbo.CrawlKeywordHits ALTER COLUMN UrlHash BINARY(32) NOT NULL;
END;
IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlKeywordHits') AND name = N'UX_CrawlKeywordHits_Url_Keyword')
    DROP INDEX UX_CrawlKeywordHits_Url_Keyword ON dbo.CrawlKeywordHits;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlKeywordHits') AND name = N'UX_CrawlKeywordHits_UrlHash_Keyword')
    CREATE UNIQUE INDEX UX_CrawlKeywordHits_UrlHash_Keyword ON dbo.CrawlKeywordHits(UrlHash, KeywordId);
