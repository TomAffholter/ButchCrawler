USE ButchCrawlerDB;

IF OBJECT_ID(N'dbo.HistoryCrawlerSubscriptions',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.HistoryCrawlerSubscriptions(
  Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HistoryCrawlerSubscriptions PRIMARY KEY,
  Name nvarchar(200) NOT NULL,
  Email nvarchar(320) NOT NULL,
  Organization nvarchar(300) NULL,
  ResearchType nvarchar(1000) NOT NULL,
  Comments nvarchar(4000) NULL,
  ConsentGiven bit NOT NULL,
  SubmittedUtc datetime2(0) NOT NULL,
  ReceivedUtc datetime2(0) NOT NULL CONSTRAINT DF_HistoryCrawlerSubscriptions_ReceivedUtc DEFAULT SYSUTCDATETIME(),
  MessageHash binary(32) NOT NULL
 );
 CREATE UNIQUE INDEX UX_HistoryCrawlerSubscriptions_MessageHash ON dbo.HistoryCrawlerSubscriptions(MessageHash);
 CREATE INDEX IX_HistoryCrawlerSubscriptions_ReceivedUtc ON dbo.HistoryCrawlerSubscriptions(ReceivedUtc DESC);
END;

IF OBJECT_ID(N'dbo.HistoryCrawlerSuggestions',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.HistoryCrawlerSuggestions(
  Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HistoryCrawlerSuggestions PRIMARY KEY,
  Name nvarchar(200) NOT NULL,
  Email nvarchar(320) NOT NULL,
  Organization nvarchar(300) NULL,
  ResearchType nvarchar(1000) NOT NULL,
  Category nvarchar(100) NULL,
  Suggestion nvarchar(4000) NOT NULL,
  MayContact bit NOT NULL,
  SubmittedUtc datetime2(0) NOT NULL,
  ReceivedUtc datetime2(0) NOT NULL CONSTRAINT DF_HistoryCrawlerSuggestions_ReceivedUtc DEFAULT SYSUTCDATETIME(),
  MessageHash binary(32) NOT NULL
 );
 CREATE UNIQUE INDEX UX_HistoryCrawlerSuggestions_MessageHash ON dbo.HistoryCrawlerSuggestions(MessageHash);
 CREATE INDEX IX_HistoryCrawlerSuggestions_ReceivedUtc ON dbo.HistoryCrawlerSuggestions(ReceivedUtc DESC);
END;
