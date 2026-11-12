-- 008_CreateCrawlArtifacts.sql
-- Preserve parsed HTML metadata and capture details for auditability.

IF NOT EXISTS (
	SELECT 1
	FROM sys.tables t
	JOIN sys.schemas s ON t.schema_id = s.schema_id
	WHERE s.name = 'dbo' AND t.name = 'CrawlArtifacts'
)
BEGIN
	CREATE TABLE dbo.CrawlArtifacts (
		Url NVARCHAR(2000) NOT NULL CONSTRAINT PK_CrawlArtifacts PRIMARY KEY,
		FinalUrl NVARCHAR(2000) NULL,
		Title NVARCHAR(1000) NULL,
		RawHtml NVARCHAR(MAX) NULL,
		JsonLd NVARCHAR(MAX) NULL,
		OpenGraphJson NVARCHAR(MAX) NULL,
		MicroformatsJson NVARCHAR(MAX) NULL,
		OutgoingLinksJson NVARCHAR(MAX) NULL,
		ContentHash NVARCHAR(128) NULL,
		FetchHeadersJson NVARCHAR(MAX) NULL,
		RedirectChainJson NVARCHAR(MAX) NULL,
		StatusCode INT NULL,
		UserAgent NVARCHAR(512) NULL,
		RemoteIp NVARCHAR(128) NULL,
		Asn NVARCHAR(128) NULL,
		Source NVARCHAR(4000) NULL,
		RetrievedAt DATETIME2 NULL,
		CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_CrawlArtifacts_CreatedAt DEFAULT SYSUTCDATETIME(),
		UpdatedAt DATETIME2 NULL
	);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlArtifacts') AND name = 'IX_CrawlArtifacts_RetrievedAt')
BEGIN
	CREATE INDEX IX_CrawlArtifacts_RetrievedAt ON dbo.CrawlArtifacts(RetrievedAt);
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlArtifacts') AND name = 'IX_CrawlArtifacts_ContentHash')
BEGIN
	CREATE INDEX IX_CrawlArtifacts_ContentHash ON dbo.CrawlArtifacts(ContentHash);
END
