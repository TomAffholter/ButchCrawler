SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID(N'dbo.ChallengeFailureHistory',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.ChallengeFailureHistory(
  Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ChallengeFailureHistory PRIMARY KEY,
  CaptchaTaskId BIGINT NULL,
  Url NVARCHAR(2000) NOT NULL,
  UrlHash AS CONVERT(VARBINARY(32),HASHBYTES('SHA2_256',CONVERT(NVARCHAR(4000),ISNULL(Url,N'')))) PERSISTED,
  SeedUrl NVARCHAR(2000) NULL,
  DetectedAt DATETIME2 NOT NULL CONSTRAINT DF_ChallengeFailureHistory_DetectedAt DEFAULT SYSUTCDATETIME(),
  HttpStatus INT NULL,
  ChallengeType NVARCHAR(100) NULL,
  FailureReason NVARCHAR(1000) NULL,
  ResolutionStatus NVARCHAR(20) NOT NULL CONSTRAINT DF_ChallengeFailureHistory_Status DEFAULT N'pending',
  ResolvedAt DATETIME2 NULL
 );
 CREATE INDEX IX_ChallengeFailureHistory_UrlHashDetectedAt ON dbo.ChallengeFailureHistory(UrlHash,DetectedAt DESC);
 CREATE INDEX IX_ChallengeFailureHistory_Status ON dbo.ChallengeFailureHistory(ResolutionStatus,DetectedAt DESC);
END;
IF COL_LENGTH(N'dbo.ChallengeFailureHistory',N'UrlHash') IS NULL
 ALTER TABLE dbo.ChallengeFailureHistory ADD UrlHash AS CONVERT(VARBINARY(32),HASHBYTES('SHA2_256',CONVERT(NVARCHAR(4000),ISNULL(Url,N'')))) PERSISTED;
IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ChallengeFailureHistory') AND name=N'IX_ChallengeFailureHistory_UrlDetectedAt')
 DROP INDEX IX_ChallengeFailureHistory_UrlDetectedAt ON dbo.ChallengeFailureHistory;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ChallengeFailureHistory') AND name=N'IX_ChallengeFailureHistory_UrlHashDetectedAt')
 CREATE INDEX IX_ChallengeFailureHistory_UrlHashDetectedAt ON dbo.ChallengeFailureHistory(UrlHash,DetectedAt DESC);

-- Preserve the challenge tasks known before per-attempt history was introduced.
INSERT dbo.ChallengeFailureHistory(CaptchaTaskId,Url,SeedUrl,DetectedAt,ChallengeType,FailureReason,ResolutionStatus,ResolvedAt)
SELECT t.Id,t.Url,t.SeedUrl,COALESCE(t.CreatedAt,SYSUTCDATETIME()),N'legacy',
 N'Challenge recorded before per-attempt history was enabled',
 CASE WHEN t.Status=N'solved' THEN N'solved' WHEN t.Status=N'ignored' THEN N'ignored' ELSE N'pending' END,t.SolvedAt
FROM dbo.CaptchaTasks t
WHERE t.Url IS NOT NULL AND NOT EXISTS(
 SELECT 1 FROM dbo.ChallengeFailureHistory h WHERE h.CaptchaTaskId=t.Id);
