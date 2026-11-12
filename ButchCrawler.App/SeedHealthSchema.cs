using Microsoft.Data.SqlClient;

namespace ButchCrawler.App;

public static class SeedHealthSchema
{
    public const string Sql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.SeedUrls',N'U') IS NULL THROW 50014, 'dbo.SeedUrls is missing.', 1;
IF COL_LENGTH(N'dbo.SeedUrls',N'HealthStatus') IS NULL ALTER TABLE dbo.SeedUrls ADD HealthStatus NVARCHAR(32) NULL;
IF COL_LENGTH(N'dbo.SeedUrls',N'ConsecutiveFailures') IS NULL ALTER TABLE dbo.SeedUrls ADD ConsecutiveFailures INT NOT NULL CONSTRAINT DF_SeedUrls_ConsecutiveFailures DEFAULT 0;
IF COL_LENGTH(N'dbo.SeedUrls',N'FirstFailureAt') IS NULL ALTER TABLE dbo.SeedUrls ADD FirstFailureAt DATETIME2 NULL;
IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureAt') IS NULL ALTER TABLE dbo.SeedUrls ADD LastFailureAt DATETIME2 NULL;
IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureStatus') IS NULL ALTER TABLE dbo.SeedUrls ADD LastFailureStatus INT NULL;
IF COL_LENGTH(N'dbo.SeedUrls',N'LastFailureReason') IS NULL ALTER TABLE dbo.SeedUrls ADD LastFailureReason NVARCHAR(1000) NULL;
IF COL_LENGTH(N'dbo.SeedUrls',N'NextHealthCheckAt') IS NULL ALTER TABLE dbo.SeedUrls ADD NextHealthCheckAt DATETIME2 NULL;
IF COL_LENGTH(N'dbo.SeedUrls',N'DisabledReason') IS NULL ALTER TABLE dbo.SeedUrls ADD DisabledReason NVARCHAR(500) NULL;
IF COL_LENGTH(N'dbo.SeedUrls',N'AutoDisabled') IS NULL ALTER TABLE dbo.SeedUrls ADD AutoDisabled BIT NOT NULL CONSTRAINT DF_SeedUrls_AutoDisabled DEFAULT 0;
IF OBJECT_ID(N'dbo.CrawlQueue',N'U') IS NOT NULL
BEGIN
 IF COL_LENGTH(N'dbo.CrawlQueue',N'Attempts') IS NULL ALTER TABLE dbo.CrawlQueue ADD Attempts INT NOT NULL CONSTRAINT DF_CrawlQueue_SeedHealthAttempts DEFAULT 0;
 IF COL_LENGTH(N'dbo.CrawlQueue',N'NextAttemptAt') IS NULL ALTER TABLE dbo.CrawlQueue ADD NextAttemptAt DATETIME2 NULL;
 IF COL_LENGTH(N'dbo.CrawlQueue',N'LeaseExpires') IS NULL ALTER TABLE dbo.CrawlQueue ADD LeaseExpires DATETIME2 NULL;
END;
IF OBJECT_ID(N'dbo.CaptchaTasks',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.CaptchaTasks(
  Id BIGINT IDENTITY PRIMARY KEY,Url NVARCHAR(2000) NOT NULL,SeedUrl NVARCHAR(2000) NULL,
  CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),Status NVARCHAR(20) NOT NULL DEFAULT N'pending',
  ScreenshotPath NVARCHAR(2000) NULL,HtmlSnippet NVARCHAR(MAX) NULL,SolutionJson NVARCHAR(MAX) NULL,
  SolvedAt DATETIME2 NULL,Attempts INT NOT NULL DEFAULT 0);
END;
ELSE
BEGIN
 IF COL_LENGTH(N'dbo.CaptchaTasks',N'Url') IS NULL ALTER TABLE dbo.CaptchaTasks ADD Url NVARCHAR(2000) NULL;
 IF COL_LENGTH(N'dbo.CaptchaTasks',N'SeedUrl') IS NULL ALTER TABLE dbo.CaptchaTasks ADD SeedUrl NVARCHAR(2000) NULL;
 IF COL_LENGTH(N'dbo.CaptchaTasks',N'CreatedAt') IS NULL ALTER TABLE dbo.CaptchaTasks ADD CreatedAt DATETIME2 NULL;
 IF COL_LENGTH(N'dbo.CaptchaTasks',N'Status') IS NULL ALTER TABLE dbo.CaptchaTasks ADD Status NVARCHAR(20) NULL;
 IF COL_LENGTH(N'dbo.CaptchaTasks',N'SolvedAt') IS NULL ALTER TABLE dbo.CaptchaTasks ADD SolvedAt DATETIME2 NULL;
 IF COL_LENGTH(N'dbo.CaptchaTasks',N'Attempts') IS NULL ALTER TABLE dbo.CaptchaTasks ADD Attempts INT NOT NULL CONSTRAINT DF_CaptchaTasks_SeedHealthAttempts DEFAULT 0;
END;";

    private const string ChallengeHistorySql = @"
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'dbo.ChallengeFailureHistory',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.ChallengeFailureHistory(
  Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChallengeFailureHistory PRIMARY KEY,
  CaptchaTaskId BIGINT NULL,Url NVARCHAR(2000) NOT NULL,
  UrlHash AS CONVERT(VARBINARY(32),HASHBYTES('SHA2_256',CONVERT(NVARCHAR(4000),ISNULL(Url,N'')))) PERSISTED,
  SeedUrl NVARCHAR(2000) NULL,
  DetectedAt DATETIME2 NOT NULL CONSTRAINT DF_ChallengeFailureHistory_DetectedAt DEFAULT SYSUTCDATETIME(),
  HttpStatus INT NULL,ChallengeType NVARCHAR(100) NULL,FailureReason NVARCHAR(1000) NULL,
  ResolutionStatus NVARCHAR(20) NOT NULL CONSTRAINT DF_ChallengeFailureHistory_Status DEFAULT N'pending',
  ResolvedAt DATETIME2 NULL);
 CREATE INDEX IX_ChallengeFailureHistory_UrlHashDetectedAt ON dbo.ChallengeFailureHistory(UrlHash,DetectedAt DESC);
 CREATE INDEX IX_ChallengeFailureHistory_Status ON dbo.ChallengeFailureHistory(ResolutionStatus,DetectedAt DESC);
END;
IF COL_LENGTH(N'dbo.ChallengeFailureHistory',N'UrlHash') IS NULL
 ALTER TABLE dbo.ChallengeFailureHistory ADD UrlHash AS CONVERT(VARBINARY(32),HASHBYTES('SHA2_256',CONVERT(NVARCHAR(4000),ISNULL(Url,N'')))) PERSISTED;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ChallengeFailureHistory') AND name=N'IX_ChallengeFailureHistory_CaptchaTaskId')
 CREATE INDEX IX_ChallengeFailureHistory_CaptchaTaskId ON dbo.ChallengeFailureHistory(CaptchaTaskId) WHERE CaptchaTaskId IS NOT NULL;
IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ChallengeFailureHistory') AND name=N'IX_ChallengeFailureHistory_UrlDetectedAt')
 DROP INDEX IX_ChallengeFailureHistory_UrlDetectedAt ON dbo.ChallengeFailureHistory;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ChallengeFailureHistory') AND name=N'IX_ChallengeFailureHistory_UrlHashDetectedAt')
 CREATE INDEX IX_ChallengeFailureHistory_UrlHashDetectedAt ON dbo.ChallengeFailureHistory(UrlHash,DetectedAt DESC);
INSERT dbo.ChallengeFailureHistory(CaptchaTaskId,Url,SeedUrl,DetectedAt,ChallengeType,FailureReason,ResolutionStatus,ResolvedAt)
SELECT t.Id,t.Url,t.SeedUrl,COALESCE(t.CreatedAt,SYSUTCDATETIME()),N'legacy',
 N'Challenge recorded before per-attempt history was enabled',
 CASE WHEN t.Status=N'solved' THEN N'solved' WHEN t.Status=N'ignored' THEN N'ignored' ELSE N'pending' END,t.SolvedAt
FROM dbo.CaptchaTasks t
WHERE t.Url IS NOT NULL AND NOT EXISTS(SELECT 1 FROM dbo.ChallengeFailureHistory h WHERE h.CaptchaTaskId=t.Id);";

    private const string ChallengeHostPauseSql = @"
IF OBJECT_ID(N'dbo.ChallengeHostPauses',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.ChallengeHostPauses(
  Host NVARCHAR(255) NOT NULL CONSTRAINT PK_ChallengeHostPauses PRIMARY KEY,
  RepresentativeUrl NVARCHAR(2000) NOT NULL,SeedId INT NULL,ChallengeType NVARCHAR(100) NULL,
  ChallengeCount INT NOT NULL CONSTRAINT DF_ChallengeHostPauses_Count DEFAULT 2,
  DeferredCount INT NOT NULL CONSTRAINT DF_ChallengeHostPauses_Deferred DEFAULT 0,
  Status NVARCHAR(20) NOT NULL CONSTRAINT DF_ChallengeHostPauses_Status DEFAULT N'paused',
  FirstChallengeAt DATETIME2 NOT NULL CONSTRAINT DF_ChallengeHostPauses_First DEFAULT SYSUTCDATETIME(),
  LastChallengeAt DATETIME2 NOT NULL CONSTRAINT DF_ChallengeHostPauses_Last DEFAULT SYSUTCDATETIME(),
  ResolvedAt DATETIME2 NULL);
 CREATE INDEX IX_ChallengeHostPauses_Status ON dbo.ChallengeHostPauses(Status,LastChallengeAt DESC);
END;";

    // This must be a separate command. SQL Server compiles column references for
    // a batch before ALTER TABLE statements in that same batch have completed.
    private const string FinalizeSql = @"
UPDATE dbo.SeedUrls SET HealthStatus=N'healthy' WHERE HealthStatus IS NULL;
UPDATE dbo.SeedUrls SET FirstFailureAt=LastFailureAt
WHERE FirstFailureAt IS NULL AND ISNULL(ConsecutiveFailures,0)>0 AND LastFailureAt IS NOT NULL;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SeedUrls') AND name=N'IX_SeedUrls_Health')
 CREATE INDEX IX_SeedUrls_Health ON dbo.SeedUrls(HealthStatus,IsActive,NextHealthCheckAt);";

    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = Sql; command.CommandTimeout = 180;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = FinalizeSql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = ChallengeHistorySql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        command.CommandText = ChallengeHostPauseSql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
