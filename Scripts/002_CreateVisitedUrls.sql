-- 002_CreateVisitedUrls.sql
-- Ensure VisitedUrls table and required columns exist

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'dbo' AND t.name = 'VisitedUrls')
BEGIN
	CREATE TABLE dbo.VisitedUrls (
		Url NVARCHAR(2000) NOT NULL PRIMARY KEY,
		ContentHash NVARCHAR(128) NULL,
		StatusCode INT NULL,
		LastSeen DATETIME2 NULL
	);
END
ELSE
BEGIN
	-- add missing columns if absent
	IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id WHERE t.name = 'VisitedUrls' AND c.name = 'ContentHash')
		ALTER TABLE dbo.VisitedUrls ADD ContentHash NVARCHAR(128) NULL;
	IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id WHERE t.name = 'VisitedUrls' AND c.name = 'StatusCode')
		ALTER TABLE dbo.VisitedUrls ADD StatusCode INT NULL;
	IF NOT EXISTS (SELECT 1 FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id WHERE t.name = 'VisitedUrls' AND c.name = 'LastSeen')
		ALTER TABLE dbo.VisitedUrls ADD LastSeen DATETIME2 NULL;
END

-- optional index on LastSeen
IF NOT EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id WHERE t.name = 'VisitedUrls' AND i.name = 'IX_VisitedUrls_LastSeen')
BEGIN
	CREATE INDEX IX_VisitedUrls_LastSeen ON dbo.VisitedUrls(LastSeen);
END
