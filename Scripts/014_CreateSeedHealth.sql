SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.SeedUrls', N'U') IS NULL
    THROW 50014, 'dbo.SeedUrls must exist before installing seed health tracking.', 1;

IF COL_LENGTH(N'dbo.SeedUrls', N'HealthStatus') IS NULL
    ALTER TABLE dbo.SeedUrls ADD HealthStatus NVARCHAR(32) NULL;
IF COL_LENGTH(N'dbo.SeedUrls', N'ConsecutiveFailures') IS NULL
    ALTER TABLE dbo.SeedUrls ADD ConsecutiveFailures INT NOT NULL CONSTRAINT DF_SeedUrls_ConsecutiveFailures DEFAULT 0;
IF COL_LENGTH(N'dbo.SeedUrls', N'FirstFailureAt') IS NULL
    ALTER TABLE dbo.SeedUrls ADD FirstFailureAt DATETIME2 NULL;
IF COL_LENGTH(N'dbo.SeedUrls', N'LastFailureAt') IS NULL
    ALTER TABLE dbo.SeedUrls ADD LastFailureAt DATETIME2 NULL;
IF COL_LENGTH(N'dbo.SeedUrls', N'LastFailureStatus') IS NULL
    ALTER TABLE dbo.SeedUrls ADD LastFailureStatus INT NULL;
IF COL_LENGTH(N'dbo.SeedUrls', N'LastFailureReason') IS NULL
    ALTER TABLE dbo.SeedUrls ADD LastFailureReason NVARCHAR(1000) NULL;
IF COL_LENGTH(N'dbo.SeedUrls', N'NextHealthCheckAt') IS NULL
    ALTER TABLE dbo.SeedUrls ADD NextHealthCheckAt DATETIME2 NULL;
IF COL_LENGTH(N'dbo.SeedUrls', N'DisabledReason') IS NULL
    ALTER TABLE dbo.SeedUrls ADD DisabledReason NVARCHAR(500) NULL;
IF COL_LENGTH(N'dbo.SeedUrls', N'AutoDisabled') IS NULL
    ALTER TABLE dbo.SeedUrls ADD AutoDisabled BIT NOT NULL CONSTRAINT DF_SeedUrls_AutoDisabled DEFAULT 0;

-- Older CrawlQueue installations did not always include the retry bookkeeping
-- columns.  The problem-seed controls use these to clear stale retry state.
IF OBJECT_ID(N'dbo.CrawlQueue', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.CrawlQueue', N'Attempts') IS NULL
        ALTER TABLE dbo.CrawlQueue ADD Attempts INT NOT NULL CONSTRAINT DF_CrawlQueue_SeedHealthAttempts DEFAULT 0;
    IF COL_LENGTH(N'dbo.CrawlQueue', N'NextAttemptAt') IS NULL
        ALTER TABLE dbo.CrawlQueue ADD NextAttemptAt DATETIME2 NULL;
    IF COL_LENGTH(N'dbo.CrawlQueue', N'LeaseExpires') IS NULL
        ALTER TABLE dbo.CrawlQueue ADD LeaseExpires DATETIME2 NULL;
END;

IF OBJECT_ID(N'dbo.CaptchaTasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CaptchaTasks (
        Id BIGINT IDENTITY PRIMARY KEY,
        Url NVARCHAR(2000) NOT NULL,
        SeedUrl NVARCHAR(2000) NULL,
        CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        Status NVARCHAR(20) NOT NULL DEFAULT N'pending',
        ScreenshotPath NVARCHAR(2000) NULL,
        HtmlSnippet NVARCHAR(MAX) NULL,
        SolutionJson NVARCHAR(MAX) NULL,
        SolvedAt DATETIME2 NULL,
        Attempts INT NOT NULL DEFAULT 0
    );
END;
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.CaptchaTasks', N'Url') IS NULL
        ALTER TABLE dbo.CaptchaTasks ADD Url NVARCHAR(2000) NULL;
    IF COL_LENGTH(N'dbo.CaptchaTasks', N'SeedUrl') IS NULL
        ALTER TABLE dbo.CaptchaTasks ADD SeedUrl NVARCHAR(2000) NULL;
    IF COL_LENGTH(N'dbo.CaptchaTasks', N'CreatedAt') IS NULL
        ALTER TABLE dbo.CaptchaTasks ADD CreatedAt DATETIME2 NULL;
    IF COL_LENGTH(N'dbo.CaptchaTasks', N'Status') IS NULL
        ALTER TABLE dbo.CaptchaTasks ADD Status NVARCHAR(20) NULL;
    IF COL_LENGTH(N'dbo.CaptchaTasks', N'SolvedAt') IS NULL
        ALTER TABLE dbo.CaptchaTasks ADD SolvedAt DATETIME2 NULL;
    IF COL_LENGTH(N'dbo.CaptchaTasks', N'Attempts') IS NULL
        ALTER TABLE dbo.CaptchaTasks ADD Attempts INT NOT NULL CONSTRAINT DF_CaptchaTasks_SeedHealthAttempts DEFAULT 0;
END;

-- Compile references to the newly added columns only after the ALTER batch.
GO

UPDATE dbo.SeedUrls SET HealthStatus = N'healthy' WHERE HealthStatus IS NULL;
UPDATE dbo.SeedUrls SET FirstFailureAt = LastFailureAt
WHERE FirstFailureAt IS NULL AND ISNULL(ConsecutiveFailures,0) > 0 AND LastFailureAt IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SeedUrls') AND name=N'IX_SeedUrls_Health')
    CREATE INDEX IX_SeedUrls_Health ON dbo.SeedUrls(HealthStatus, IsActive, NextHealthCheckAt);
