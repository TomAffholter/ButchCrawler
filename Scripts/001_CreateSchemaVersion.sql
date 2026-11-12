-- 001_CreateSchemaVersion.sql
-- Create schema versioning table and set initial version = 1

IF NOT EXISTS (SELECT 1 FROM sys.schemas s JOIN sys.tables t ON s.schema_id = t.schema_id WHERE s.name = 'dbo' AND t.name = 'SchemaVersion')
BEGIN
	CREATE TABLE dbo.SchemaVersion (
		Version INT NOT NULL PRIMARY KEY,
		AppliedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
		Description NVARCHAR(200) NULL
	);
END

-- Insert initial version marker if not present
IF NOT EXISTS (SELECT 1 FROM dbo.SchemaVersion WHERE Version = 1)
BEGIN
	INSERT INTO dbo.SchemaVersion (Version, AppliedAt, Description) VALUES (1, SYSUTCDATETIME(), 'Initial schema for ButchCrawler v1');
END
