-- 005_AddCaptchaTables.sql
-- Create captcha tables in SQL Server compatible, idempotent way

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'dbo' AND t.name = 'CaptchaTasks')
BEGIN
	CREATE TABLE dbo.CaptchaTasks (
		Id BIGINT IDENTITY PRIMARY KEY,
		Url NVARCHAR(2000) NOT NULL,
		SeedUrl NVARCHAR(2000) NULL,
		CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
		Status NVARCHAR(20) NOT NULL DEFAULT 'pending',
		ScreenshotPath NVARCHAR(2000) NULL,
		HtmlSnippet NVARCHAR(MAX) NULL,
		SolutionJson NVARCHAR(MAX) NULL,
		SolvedAt DATETIME2 NULL,
		Attempts INT NOT NULL DEFAULT 0
	);
END

IF COL_LENGTH('dbo.CaptchaTasks', 'ReuseUntil') IS NULL
    ALTER TABLE dbo.CaptchaTasks ADD ReuseUntil DATETIME2 NULL;
IF COL_LENGTH('dbo.CaptchaTasks', 'updated_at') IS NULL
    ALTER TABLE dbo.CaptchaTasks ADD updated_at DATETIME2 NULL;

IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'dbo' AND t.name = 'CaptchaSolutions')
BEGIN
	CREATE TABLE dbo.CaptchaSolutions (
		Id BIGINT IDENTITY PRIMARY KEY,
		Domain NVARCHAR(256) NOT NULL,
		SolutionJson NVARCHAR(MAX) NOT NULL,
		CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
		ExpiresAt DATETIME2 NULL
	);
END
