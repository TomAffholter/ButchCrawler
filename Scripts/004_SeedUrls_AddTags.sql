-- 004_SeedUrls_AddTags.sql
-- Ensure SeedUrls table has Tags column

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'dbo' AND t.name = 'SeedUrls')
BEGIN
	-- minimal SeedUrls table if missing
	CREATE TABLE dbo.SeedUrls (
		Id INT IDENTITY PRIMARY KEY,
		Url NVARCHAR(2000) NOT NULL,
		Domain NVARCHAR(256) NULL,
		Category NVARCHAR(200) NULL,
		Priority INT NULL,
		MaxDepth INT NULL,
		FollowExternal BIT NULL,
		IsActive BIT NULL,
		Notes NVARCHAR(MAX) NULL,
		CreatedAt DATETIME2 NULL,
		LastCrawledAt DATETIME2 NULL,
		SourceType NVARCHAR(100) NULL,
		DiscoverySource NVARCHAR(100) NULL,
		Tags NVARCHAR(500) NULL
	);
END
ELSE
BEGIN
	IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id WHERE t.name = 'SeedUrls' AND c.name = 'Tags')
		ALTER TABLE dbo.SeedUrls ADD Tags NVARCHAR(500) NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id WHERE t.name = 'SeedUrls' AND i.name = 'IX_SeedUrls_Url')
BEGIN
	CREATE INDEX IX_SeedUrls_Url ON dbo.SeedUrls(Url);
END
