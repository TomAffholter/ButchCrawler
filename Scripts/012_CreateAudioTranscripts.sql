SET NOCOUNT ON;
SET XACT_ABORT ON;
IF OBJECT_ID(N'dbo.AudioTranscripts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AudioTranscripts
    (
        Id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_AudioTranscripts PRIMARY KEY,
        Source NVARCHAR(2000) NOT NULL,
        SourceHash BINARY(32) NOT NULL,
        ContentHash BINARY(32) NOT NULL,
        Model NVARCHAR(128) NOT NULL,
        Transcript NVARCHAR(MAX) NOT NULL,
        SegmentsJson NVARCHAR(MAX) NOT NULL,
        IsComplete BIT NOT NULL CONSTRAINT DF_AudioTranscripts_Complete DEFAULT 1,
        ChunkSeconds INT NOT NULL CONSTRAINT DF_AudioTranscripts_ChunkSeconds DEFAULT 0,
        TranscribedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_AudioTranscripts_At DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UX_AudioTranscripts_Source_Content_Model UNIQUE(SourceHash,ContentHash,Model)
    );
END;
IF COL_LENGTH(N'dbo.AudioTranscripts',N'IsComplete') IS NULL
    ALTER TABLE dbo.AudioTranscripts ADD IsComplete BIT NOT NULL CONSTRAINT DF_AudioTranscripts_Complete DEFAULT 1;
IF COL_LENGTH(N'dbo.AudioTranscripts',N'ChunkSeconds') IS NULL
    ALTER TABLE dbo.AudioTranscripts ADD ChunkSeconds INT NOT NULL CONSTRAINT DF_AudioTranscripts_ChunkSeconds DEFAULT 0;
