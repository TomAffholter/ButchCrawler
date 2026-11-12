-- 007_CrawlQueue_IdIntToBigint.sql
-- Manual/safe conversion for dbo.CrawlQueue.Id from INT IDENTITY to BIGINT IDENTITY
-- Run during maintenance window.

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.CrawlQueue', N'U') IS NULL
BEGIN
	PRINT 'dbo.CrawlQueue does not exist. Nothing to convert.';
	RETURN;
END;

IF EXISTS (
	SELECT 1
	FROM sys.columns c
	JOIN sys.types t ON t.user_type_id = c.user_type_id
	WHERE c.object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U')
	  AND c.name = N'id'
	  AND t.name = N'bigint'
)
BEGIN
	PRINT 'dbo.CrawlQueue.id is already BIGINT. Nothing to convert.';
	RETURN;
END;

IF EXISTS (
	SELECT 1
	FROM sys.foreign_keys fk
	WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U')
)
BEGIN
	RAISERROR('dbo.CrawlQueue is referenced by one or more foreign keys. Drop/recreate those FKs before running this conversion.', 16, 1);
	RETURN;
END;

BEGIN TRAN;

-- Recreate table with BIGINT identity and expected columns
CREATE TABLE dbo.CrawlQueue_new (
	id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrawlQueue_new PRIMARY KEY,
	url NVARCHAR(2000) NOT NULL,
	status NVARCHAR(50) NULL,
	attempts INT NULL,
	locked_until DATETIME2 NULL,
	last_error NVARCHAR(MAX) NULL,
	created_at DATETIME2 NOT NULL CONSTRAINT DF_CrawlQueue_new_created_at DEFAULT SYSUTCDATETIME(),
	updated_at DATETIME2 NULL,
	Claimed BIT NULL,
	LeaseExpires DATETIME2 NULL,
	discovered_from NVARCHAR(2000) NULL,
	discovered_at DATETIME2 NULL,
	SeedID INT NULL,
	SeedIdNormalized NVARCHAR(2000) NULL,
	SeedURL NVARCHAR(2000) NULL,
	Scope NVARCHAR(2000) NULL,
	Depth INT NULL,
	ParentUrl NVARCHAR(2000) NULL,
	LockToken UNIQUEIDENTIFIER NULL,
	UrlHash VARBINARY(32) NULL,
	SeedUrlHash VARBINARY(32) NULL
);

SET IDENTITY_INSERT dbo.CrawlQueue_new ON;

INSERT INTO dbo.CrawlQueue_new
(
	id, url, status, attempts, locked_until, last_error, created_at, updated_at,
	Claimed, LeaseExpires, discovered_from, discovered_at, SeedID, SeedIdNormalized,
	SeedURL, Scope, Depth, ParentUrl, LockToken, UrlHash, SeedUrlHash
)
SELECT
	CONVERT(BIGINT, id), url, status, attempts, locked_until, last_error, created_at, updated_at,
	Claimed, LeaseExpires, discovered_from, discovered_at, SeedID, SeedIdNormalized,
	SeedURL, Scope, Depth, ParentUrl, LockToken, UrlHash, SeedUrlHash
FROM dbo.CrawlQueue WITH (HOLDLOCK, TABLOCKX);

SET IDENTITY_INSERT dbo.CrawlQueue_new OFF;

DROP TABLE dbo.CrawlQueue;
EXEC sp_rename N'dbo.CrawlQueue_new', N'CrawlQueue';

-- Recreate indexes used by crawler code
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U') AND name = N'IX_CrawlQueue_Status')
	CREATE INDEX IX_CrawlQueue_Status ON dbo.CrawlQueue(status);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U') AND name = N'IX_CrawlQueue_SeedId')
	CREATE INDEX IX_CrawlQueue_SeedId ON dbo.CrawlQueue(SeedID);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U') AND name = N'IX_CrawlQueue_SeedUrlHash')
	CREATE INDEX IX_CrawlQueue_SeedUrlHash ON dbo.CrawlQueue(SeedUrlHash);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U') AND name = N'IX_CrawlQueue_LockedUntil')
	CREATE INDEX IX_CrawlQueue_LockedUntil ON dbo.CrawlQueue(locked_until);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U') AND name = N'UX_CrawlQueue_Url_SeedId')
	CREATE UNIQUE INDEX UX_CrawlQueue_Url_SeedId ON dbo.CrawlQueue(UrlHash, SeedID) WHERE SeedID IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U') AND name = N'UX_CrawlQueue_Url_SeedUrl_NoSeedId')
	CREATE UNIQUE INDEX UX_CrawlQueue_Url_SeedUrl_NoSeedId ON dbo.CrawlQueue(UrlHash, SeedUrlHash) WHERE SeedID IS NULL AND SeedURL IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CrawlQueue', N'U') AND name = N'UX_CrawlQueue_Url_NoScope')
	CREATE UNIQUE INDEX UX_CrawlQueue_Url_NoScope ON dbo.CrawlQueue(UrlHash) WHERE SeedID IS NULL AND SeedURL IS NULL;

COMMIT TRAN;

PRINT 'Conversion complete: dbo.CrawlQueue.id is now BIGINT.';
