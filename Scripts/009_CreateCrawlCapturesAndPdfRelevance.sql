-- 009_CreateCrawlCapturesAndPdfRelevance.sql
-- Immutable capture/version history + PDF relevance annotations (idempotent)

IF NOT EXISTS (
	SELECT 1
	FROM sys.tables t
	JOIN sys.schemas s ON s.schema_id = t.schema_id
	WHERE s.name = 'dbo' AND t.name = 'CrawlCaptures'
)
BEGIN
	CREATE TABLE dbo.CrawlCaptures (
		Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrawlCaptures PRIMARY KEY,
		Url NVARCHAR(2000) NOT NULL,
		FinalUrl NVARCHAR(2000) NULL,
		CaptureType NVARCHAR(64) NOT NULL CONSTRAINT DF_CrawlCaptures_CaptureType DEFAULT N'http',
		StatusCode INT NULL,
		BodyText NVARCHAR(MAX) NULL,
		BodyTextFull NVARCHAR(MAX) NULL,
		Title NVARCHAR(1024) NULL,
		ContentHash NVARCHAR(128) NULL,
		ETag NVARCHAR(512) NULL,
		LastModified NVARCHAR(256) NULL,
		WasNotModified BIT NOT NULL CONSTRAINT DF_CrawlCaptures_WasNotModified DEFAULT 0,
		UserAgent NVARCHAR(512) NULL,
		RemoteIp NVARCHAR(64) NULL,
		Asn NVARCHAR(128) NULL,
		Source NVARCHAR(512) NULL,
		RetrievedAt DATETIME2 NOT NULL,
		CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_CrawlCaptures_CreatedAt DEFAULT SYSUTCDATETIME()
	);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlCaptures') AND name = 'IX_CrawlCaptures_UrlRetrievedAt')
BEGIN
	CREATE INDEX IX_CrawlCaptures_UrlRetrievedAt ON dbo.CrawlCaptures(Url, RetrievedAt DESC);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlCaptures') AND name = 'IX_CrawlCaptures_ContentHash')
BEGIN
	CREATE INDEX IX_CrawlCaptures_ContentHash ON dbo.CrawlCaptures(ContentHash);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlCaptures') AND name = 'IX_CrawlCaptures_ETagLastModified')
BEGIN
	CREATE INDEX IX_CrawlCaptures_ETagLastModified ON dbo.CrawlCaptures(Url, ETag, LastModified);
END

IF NOT EXISTS (
	SELECT 1
	FROM sys.tables t
	JOIN sys.schemas s ON s.schema_id = t.schema_id
	WHERE s.name = 'dbo' AND t.name = 'PdfRelevanceAnnotations'
)
BEGIN
	CREATE TABLE dbo.PdfRelevanceAnnotations (
		Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PdfRelevanceAnnotations PRIMARY KEY,
		CaptureId BIGINT NOT NULL,
		MatchToken NVARCHAR(256) NOT NULL,
		Category NVARCHAR(64) NOT NULL,
		StartIndex INT NOT NULL,
		MatchLength INT NOT NULL,
		Score FLOAT NOT NULL,
		Snippet NVARCHAR(1200) NULL,
		CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_PdfRelevanceAnnotations_CreatedAt DEFAULT SYSUTCDATETIME(),
		CONSTRAINT FK_PdfRelevanceAnnotations_CrawlCaptures FOREIGN KEY (CaptureId) REFERENCES dbo.CrawlCaptures(Id)
	);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PdfRelevanceAnnotations') AND name = 'IX_PdfRelevanceAnnotations_CaptureId')
BEGIN
	CREATE INDEX IX_PdfRelevanceAnnotations_CaptureId ON dbo.PdfRelevanceAnnotations(CaptureId);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.PdfRelevanceAnnotations') AND name = 'IX_PdfRelevanceAnnotations_Token')
BEGIN
	CREATE INDEX IX_PdfRelevanceAnnotations_Token ON dbo.PdfRelevanceAnnotations(MatchToken);
END

-- Raise schema version marker for the new durability model.
IF OBJECT_ID('dbo.SchemaVersion', 'U') IS NOT NULL
BEGIN
	IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersion WHERE Version = 2)
	BEGIN
		INSERT INTO dbo.SchemaVersion(Version, AppliedAt, Description)
		VALUES (2, SYSUTCDATETIME(), 'Immutable crawl capture history and PDF relevance annotations');
	END
END
