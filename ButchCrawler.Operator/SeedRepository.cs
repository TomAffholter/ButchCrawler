using System.Data;
using ButchCrawler.App;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed class SeedRepository
{
    private readonly string _connectionString;
    private readonly bool _keywords;
    public SeedRepository(string connectionString, bool keywords)
    { _connectionString = connectionString; _keywords = keywords; }
    private string SelectSql => _keywords
        ? "SELECT Id, Keyword, Source, IsActive, CreatedAt FROM dbo.SeedKeywords"
        : "SELECT Id, Url, Domain, Category, Priority, MaxDepth, FollowExternal, IsActive, Notes, CreatedAt, LastCrawledAt, SourceType, DiscoverySource, Tags FROM dbo.SeedUrls";


    public Task<DataTable> LoadAsync() => Task.Run(async () =>
    {
                if (!_keywords) await SeedHealthSchema.EnsureAsync(_connectionString);
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                using var adapter = new SqlDataAdapter(SelectSql, connection) { MissingSchemaAction = MissingSchemaAction.AddWithKey };
                var data = new DataTable(); adapter.Fill(data);
                var id = data.Columns["Id"]!;
                id.AutoIncrement = true; id.AutoIncrementSeed = -1; id.AutoIncrementStep = -1;
                return data;
    });

    public Task SaveAsync(DataTable changes) => Task.Run(async () =>
    {
                if (!_keywords) await SeedHealthSchema.EnsureAsync(_connectionString);
                using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync();
                using var transaction = connection.BeginTransaction();
                using var adapter = new SqlDataAdapter(SelectSql, connection) { AcceptChangesDuringUpdate = false };
                adapter.SelectCommand!.Transaction = transaction;
                using var builder = new SqlCommandBuilder(adapter) { QuotePrefix = "[", QuoteSuffix = "]" };
                adapter.InsertCommand = builder.GetInsertCommand();
                adapter.UpdateCommand = builder.GetUpdateCommand();
                adapter.DeleteCommand = builder.GetDeleteCommand();
                foreach (var command in new[] { adapter.InsertCommand, adapter.UpdateCommand, adapter.DeleteCommand }) command.Transaction = transaction;
                if (_keywords)
                {
                    foreach (DataRow row in changes.Rows)
                    {
                        if (row.RowState != DataRowState.Deleted &&
                            !(row.RowState == DataRowState.Modified && !Equals(row["Keyword", DataRowVersion.Original], row["Keyword"]))) continue;
                        using var cleanup = new SqlCommand("IF OBJECT_ID(N'dbo.CrawlKeywordHits', N'U') IS NOT NULL DELETE FROM dbo.CrawlKeywordHits WHERE KeywordId = @id", connection, transaction);
                        cleanup.Parameters.Add("@id", SqlDbType.Int).Value = row["Id", DataRowVersion.Original];
                        await cleanup.ExecuteNonQueryAsync();
                    }
                }
                var activeChanges = !_keywords
                    ? changes.Rows.Cast<DataRow>()
                        .Where(row => row.RowState == DataRowState.Modified &&
                            !Equals(row["IsActive", DataRowVersion.Original], row["IsActive", DataRowVersion.Current]))
                        .Select(row => (Id: Convert.ToInt32(row["Id", DataRowVersion.Original]), Active: Convert.ToBoolean(row["IsActive", DataRowVersion.Current])))
                        .ToList()
                    : new List<(int Id, bool Active)>();
                adapter.Update(changes);
                foreach (var change in activeChanges)
                {
                    using var state = new SqlCommand(change.Active ? @"
UPDATE dbo.SeedUrls SET HealthStatus=N'retrying',AutoDisabled=0,DisabledReason=NULL,NextHealthCheckAt=NULL WHERE Id=@id;
UPDATE dbo.CrawlQueue SET Status=N'pending',Attempts=0,LockToken=NULL,locked_until=NULL,NextAttemptAt=NULL,LeaseExpires=NULL,updated_at=SYSUTCDATETIME()
WHERE SeedId=@id AND Depth=0;" : @"
UPDATE dbo.SeedUrls SET HealthStatus=N'disabled',AutoDisabled=0,DisabledReason=N'Disabled manually in Seed URLs',NextHealthCheckAt=NULL WHERE Id=@id;
UPDATE dbo.CrawlQueue SET Status=N'dead-letter',LockToken=NULL,locked_until=NULL,updated_at=SYSUTCDATETIME()
WHERE SeedId=@id AND Status=N'pending';", connection, transaction);
                    state.Parameters.AddWithValue("@id", change.Id);
                    await state.ExecuteNonQueryAsync();
                }
                transaction.Commit();
    });
}
