using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed record CommunityResponseRow(long Id, string Type, string Name, string Email, string Organization,
    string ResearchType, string Category, string Message, DateTime SubmittedUtc, DateTime ReceivedUtc);

public sealed record CommunityResponseCounts(long Subscriptions, long Suggestions);

public sealed class CommunityResponseRepository
{
    private readonly string _connectionString;

    public CommunityResponseRepository(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 15 };
        _connectionString = builder.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 60;
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ImportAsync(CommunitySubmission submission, CancellationToken cancellationToken = default)
    {
        CommunitySubmissionCodec.Validate(submission);
        await InitializeAsync(cancellationToken);
        var hash = MessageHash(submission);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = submission.Type == "subscription" ? SubscriptionInsertSql : SuggestionInsertSql;
        AddCommonParameters(command, submission, hash);
        if (submission.Type == "subscription")
        {
            command.Parameters.Add("@Comments", SqlDbType.NVarChar, 4000).Value = NullIfEmpty(submission.Message);
            command.Parameters.Add("@ConsentGiven", SqlDbType.Bit).Value = submission.ConsentGiven;
        }
        else
        {
            command.Parameters.Add("@Category", SqlDbType.NVarChar, 100).Value = NullIfEmpty(submission.Category);
            command.Parameters.Add("@Suggestion", SqlDbType.NVarChar, 4000).Value = submission.Message;
            command.Parameters.Add("@MayContact", SqlDbType.Bit).Value = submission.MayContact;
        }
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<(CommunityResponseCounts Counts, IReadOnlyList<CommunityResponseRow> Rows)> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = @"
SELECT (SELECT COUNT_BIG(*) FROM dbo.HistoryCrawlerSubscriptions),
       (SELECT COUNT_BIG(*) FROM dbo.HistoryCrawlerSuggestions);
SELECT TOP(250) Id,ResponseType,Name,Email,Organization,ResearchType,Category,Message,SubmittedUtc,ReceivedUtc
FROM (
 SELECT Id,N'Subscription' ResponseType,Name,Email,Organization,ResearchType,CAST(NULL AS nvarchar(100)) Category,
        Comments Message,SubmittedUtc,ReceivedUtc
 FROM dbo.HistoryCrawlerSubscriptions
 UNION ALL
 SELECT Id,N'Suggestion',Name,Email,Organization,ResearchType,Category,Suggestion,SubmittedUtc,ReceivedUtc
 FROM dbo.HistoryCrawlerSuggestions
) responses
ORDER BY ReceivedUtc DESC;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Response totals were not returned.");
        var counts = new CommunityResponseCounts(reader.GetInt64(0), reader.GetInt64(1));
        await reader.NextResultAsync(cancellationToken);
        var rows = new List<CommunityResponseRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CommunityResponseRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), reader.GetDateTime(8), reader.GetDateTime(9)));
        }
        return (counts, rows);
    }

    private static string Text(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    private static object NullIfEmpty(string value) => value.Length == 0 ? DBNull.Value : value;

    private static void AddCommonParameters(SqlCommand command, CommunitySubmission submission, byte[] hash)
    {
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = submission.Name;
        command.Parameters.Add("@Email", SqlDbType.NVarChar, 320).Value = submission.Email;
        command.Parameters.Add("@Organization", SqlDbType.NVarChar, 300).Value = NullIfEmpty(submission.Organization);
        command.Parameters.Add("@ResearchType", SqlDbType.NVarChar, 1000).Value = submission.ResearchType;
        command.Parameters.Add("@SubmittedUtc", SqlDbType.DateTime2).Value = submission.SubmittedUtc;
        command.Parameters.Add("@MessageHash", SqlDbType.Binary, 32).Value = hash;
    }

    private static byte[] MessageHash(CommunitySubmission submission)
    {
        var canonical = JsonSerializer.Serialize(submission);
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private const string SubscriptionInsertSql = @"
IF EXISTS(SELECT 1 FROM dbo.HistoryCrawlerSubscriptions WHERE MessageHash=@MessageHash) SELECT 0;
ELSE BEGIN
 INSERT dbo.HistoryCrawlerSubscriptions(Name,Email,Organization,ResearchType,Comments,ConsentGiven,SubmittedUtc,MessageHash)
 VALUES(@Name,@Email,@Organization,@ResearchType,@Comments,@ConsentGiven,@SubmittedUtc,@MessageHash);
 SELECT 1;
END";

    private const string SuggestionInsertSql = @"
IF EXISTS(SELECT 1 FROM dbo.HistoryCrawlerSuggestions WHERE MessageHash=@MessageHash) SELECT 0;
ELSE BEGIN
 INSERT dbo.HistoryCrawlerSuggestions(Name,Email,Organization,ResearchType,Category,Suggestion,MayContact,SubmittedUtc,MessageHash)
 VALUES(@Name,@Email,@Organization,@ResearchType,@Category,@Suggestion,@MayContact,@SubmittedUtc,@MessageHash);
 SELECT 1;
END";

    public const string SchemaSql = @"
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
END;";
}
