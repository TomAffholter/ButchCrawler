SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.ChallengeHostPauses',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.ChallengeHostPauses(
  Host NVARCHAR(255) NOT NULL CONSTRAINT PK_ChallengeHostPauses PRIMARY KEY,
  RepresentativeUrl NVARCHAR(2000) NOT NULL,
  SeedId INT NULL,
  ChallengeType NVARCHAR(100) NULL,
  ChallengeCount INT NOT NULL CONSTRAINT DF_ChallengeHostPauses_Count DEFAULT 2,
  DeferredCount INT NOT NULL CONSTRAINT DF_ChallengeHostPauses_Deferred DEFAULT 0,
  Status NVARCHAR(20) NOT NULL CONSTRAINT DF_ChallengeHostPauses_Status DEFAULT N'paused',
  FirstChallengeAt DATETIME2 NOT NULL CONSTRAINT DF_ChallengeHostPauses_First DEFAULT SYSUTCDATETIME(),
  LastChallengeAt DATETIME2 NOT NULL CONSTRAINT DF_ChallengeHostPauses_Last DEFAULT SYSUTCDATETIME(),
  ResolvedAt DATETIME2 NULL
 );
 CREATE INDEX IX_ChallengeHostPauses_Status ON dbo.ChallengeHostPauses(Status,LastChallengeAt DESC);
END;
