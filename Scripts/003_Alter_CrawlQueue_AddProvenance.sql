-- 003_Alter_CrawlQueue_AddProvenance.sql
-- SQL Server, idempotent

IF NOT EXISTS (
	SELECT 1
	FROM sys.tables t
	JOIN sys.schemas s ON t.schema_id = s.schema_id
	WHERE s.name = 'dbo' AND t.name = 'CrawlQueue'
)
BEGIN
	CREATE TABLE dbo.CrawlQueue (
		Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrawlQueue PRIMARY KEY,
		Url NVARCHAR(2000) NOT NULL,
		SeedId INT NULL,
		SeedUrl NVARCHAR(2000) NULL,
		ParentUrl NVARCHAR(2000) NULL,
		Depth INT NULL,
		Status NVARCHAR(50) NULL,
		Locked_Until DATETIME2 NULL,
		LockToken UNIQUEIDENTIFIER NULL,
		Created_At DATETIME2 NOT NULL CONSTRAINT DF_CrawlQueue_CreatedAt DEFAULT SYSUTCDATETIME(),
		Updated_At DATETIME2 NULL
	);
END
ELSE
BEGIN
	IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'SeedId')
		ALTER TABLE dbo.CrawlQueue ADD SeedId INT NULL;

	IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'SeedUrl')
		ALTER TABLE dbo.CrawlQueue ADD SeedUrl NVARCHAR(2000) NULL;

	IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'ParentUrl')
		ALTER TABLE dbo.CrawlQueue ADD ParentUrl NVARCHAR(2000) NULL;

	IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'Depth')
		ALTER TABLE dbo.CrawlQueue ADD Depth INT NULL;

	IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'LockToken')
		ALTER TABLE dbo.CrawlQueue ADD LockToken UNIQUEIDENTIFIER NULL;

	IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'Updated_At')
		ALTER TABLE dbo.CrawlQueue ADD Updated_At DATETIME2 NULL;

	IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'UrlHash')
		ALTER TABLE dbo.CrawlQueue ADD UrlHash AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', UPPER(CONVERT(NVARCHAR(4000), ISNULL(Url, N''))))) PERSISTED;

	IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'SeedUrlHash')
		ALTER TABLE dbo.CrawlQueue ADD SeedUrlHash AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', UPPER(CONVERT(NVARCHAR(4000), ISNULL(SeedUrl, N''))))) PERSISTED;

	-- If Id is INT, convert to BIGINT (IDENTITY cannot be specified in ALTER COLUMN)
	IF EXISTS (
		SELECT 1
		FROM sys.columns
		WHERE object_id = OBJECT_ID('dbo.CrawlQueue')
		  AND name = 'Id'
		  AND system_type_id = 56
	)
	BEGIN
		BEGIN TRY
			ALTER TABLE dbo.CrawlQueue ALTER COLUMN Id BIGINT NOT NULL;
		END TRY
		BEGIN CATCH
			PRINT 'INFO: Could not alter CrawlQueue.Id to BIGINT automatically. Manual migration may be required if Id is still INT.';
		END CATCH
	END
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'UrlHash')
	ALTER TABLE dbo.CrawlQueue ADD UrlHash AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', UPPER(CONVERT(NVARCHAR(4000), ISNULL(Url, N''))))) PERSISTED;

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'SeedUrlHash')
	ALTER TABLE dbo.CrawlQueue ADD SeedUrlHash AS CONVERT(VARBINARY(32), HASHBYTES('SHA2_256', UPPER(CONVERT(NVARCHAR(4000), ISNULL(SeedUrl, N''))))) PERSISTED;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'IX_CrawlQueue_Status')
	CREATE INDEX IX_CrawlQueue_Status ON dbo.CrawlQueue(Status);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'IX_CrawlQueue_SeedId')
	CREATE INDEX IX_CrawlQueue_SeedId ON dbo.CrawlQueue(SeedId);

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'IX_CrawlQueue_SeedUrl')
	DROP INDEX IX_CrawlQueue_SeedUrl ON dbo.CrawlQueue;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'IX_CrawlQueue_SeedUrlHash')
	CREATE INDEX IX_CrawlQueue_SeedUrlHash ON dbo.CrawlQueue(SeedUrlHash);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'IX_CrawlQueue_LockedUntil')
	CREATE INDEX IX_CrawlQueue_LockedUntil ON dbo.CrawlQueue(Locked_Until);

-- Scope-aware uniqueness with duplicate guards
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'UX_CrawlQueue_Url_SeedId')
	DROP INDEX UX_CrawlQueue_Url_SeedId ON dbo.CrawlQueue;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'UX_CrawlQueue_Url_SeedUrl_NoSeedId')
	DROP INDEX UX_CrawlQueue_Url_SeedUrl_NoSeedId ON dbo.CrawlQueue;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'UX_CrawlQueue_Url_NoScope')
	DROP INDEX UX_CrawlQueue_Url_NoScope ON dbo.CrawlQueue;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'UX_CrawlQueue_Url_SeedId')
BEGIN
	BEGIN TRY
		EXEC(N'CREATE UNIQUE INDEX UX_CrawlQueue_Url_SeedId ON dbo.CrawlQueue(UrlHash, SeedId) WHERE SeedId IS NOT NULL;');
	END TRY
	BEGIN CATCH
		PRINT 'WARNING: Skipping UX_CrawlQueue_Url_SeedId (missing hash column or duplicate data).';
	END CATCH
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'UX_CrawlQueue_Url_SeedUrl_NoSeedId')
BEGIN
	BEGIN TRY
		EXEC(N'CREATE UNIQUE INDEX UX_CrawlQueue_Url_SeedUrl_NoSeedId ON dbo.CrawlQueue(UrlHash, SeedUrlHash) WHERE SeedId IS NULL AND SeedUrl IS NOT NULL;');
	END TRY
	BEGIN CATCH
		PRINT 'WARNING: Skipping UX_CrawlQueue_Url_SeedUrl_NoSeedId (missing hash column or duplicate data).';
	END CATCH
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.CrawlQueue') AND name = 'UX_CrawlQueue_Url_NoScope')
BEGIN
	BEGIN TRY
		EXEC(N'CREATE UNIQUE INDEX UX_CrawlQueue_Url_NoScope ON dbo.CrawlQueue(UrlHash) WHERE SeedId IS NULL AND SeedUrl IS NULL;');
	END TRY
	BEGIN CATCH
		PRINT 'WARNING: Skipping UX_CrawlQueue_Url_NoScope (missing hash column or duplicate data).';
	END CATCH
END
