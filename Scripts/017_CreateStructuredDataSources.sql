-- Structured dataset provenance. Idempotent; does not alter collected content.
IF OBJECT_ID(N'dbo.StructuredDataSources', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StructuredDataSources
    (
        SourceHash BINARY(32) NOT NULL CONSTRAINT PK_StructuredDataSources PRIMARY KEY,
        Source NVARCHAR(2000) NOT NULL,
        Format NVARCHAR(40) NOT NULL,
        Category NVARCHAR(100) NOT NULL,
        ContentBytes BIGINT NOT NULL,
        RecordCount BIGINT NULL,
        TableCount INT NULL,
        EntryCount INT NULL,
        WasTruncated BIT NOT NULL CONSTRAINT DF_StructuredDataSources_WasTruncated DEFAULT 0,
        Warning NVARCHAR(2000) NULL,
        ContentHash CHAR(64) NOT NULL,
        ParentSource NVARCHAR(2000) NULL,
        ImportedAtUtc DATETIME2 NOT NULL CONSTRAINT DF_StructuredDataSources_ImportedAtUtc DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc DATETIME2 NULL
    );
    CREATE INDEX IX_StructuredDataSources_Format ON dbo.StructuredDataSources(Format, ImportedAtUtc DESC);
END;

