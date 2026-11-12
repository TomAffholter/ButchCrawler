using System.Data;
using System.IO;
using ButchCrawler.Operator;
using Microsoft.Data.SqlClient;

internal static class SqlIntegration
{
    public static async Task RunAsync(string root)
    {
        var database = "ButchCrawlerReview_" + Guid.NewGuid().ToString("N");
        var dataDirectory = Path.Combine(Path.GetFullPath(root), "Tests", "work");
        Directory.CreateDirectory(dataDirectory);
        var masterString = new SqlConnectionStringBuilder { DataSource = Environment.GetEnvironmentVariable("BUTCHCRAWLER_TEST_SERVER") ?? "(localdb)\\MSSQLLocalDB",
            InitialCatalog = "master", IntegratedSecurity = true, Encrypt = false, ConnectTimeout = 5 }.ConnectionString;
        await using var master = new SqlConnection(masterString); await master.OpenAsync();
        async Task Exec(SqlConnection connection, string sql)
        { using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 }; await command.ExecuteNonQueryAsync(); }
        var created = false;
        try
        {
            await Exec(master, $"CREATE DATABASE [{database}] ON PRIMARY (NAME=N'{database}', FILENAME=N'{Path.Combine(dataDirectory, database + ".mdf").Replace("'", "''")}') LOG ON (NAME=N'{database}_log', FILENAME=N'{Path.Combine(dataDirectory, database + ".ldf").Replace("'", "''")}')");
            created = true;
            var connectionString = new SqlConnectionStringBuilder(masterString) { InitialCatalog = database }.ConnectionString;
            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await Exec(connection, "SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF;");
                await Exec(connection, "CREATE TABLE dbo.SeedKeywords (Id INT IDENTITY PRIMARY KEY, Keyword NVARCHAR(200) NOT NULL UNIQUE, Source NVARCHAR(100) NULL, IsActive BIT NOT NULL DEFAULT 1, CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());");
                foreach (var filename in new[] { "003_Alter_CrawlQueue_AddProvenance.sql", "004_SeedUrls_AddTags.sql", "008_CreateCrawlArtifacts.sql", "010_CreateDocumentKeywordHits.sql" })
                {
                    var sql = await File.ReadAllTextAsync(Path.Combine(root, "Scripts", filename));
                    await Exec(connection, sql.Replace("USE ButchCrawlerDB;", ""));
                }
                var migration = await File.ReadAllTextAsync(Path.Combine(root, "Scripts", "011_PreserveUrlIdentity.sql"));
                await Exec(connection, migration); await Exec(connection, migration);
                await Exec(connection, "INSERT dbo.CrawlQueue(Url,SeedId,Status) VALUES (N'https://example.com/A',1,N'pending'),(N'https://example.com/a',1,N'pending'); INSERT dbo.CrawlArtifacts(Url) VALUES(N'https://example.com/A'),(N'https://example.com/a');");
                using var count = new SqlCommand("SELECT COUNT(DISTINCT UrlHash) FROM dbo.CrawlQueue", connection);
                if (Convert.ToInt32(await count.ExecuteScalarAsync()) != 2) throw new Exception("URL hashes collide");
                Console.WriteLine("PASS: URL migration, idempotence, distinct computed hashes and URL primary keys");
            }
            foreach (var keywords in new[] { false, true })
            {
                var repository = new SeedRepository(connectionString, keywords);
                var table = await repository.LoadAsync();
                var row = table.NewRow(); row[keywords ? "Keyword" : "Url"] = keywords ? "alpha" : "https://example.com/seed";
                row["CreatedAt"] = DateTime.UtcNow; row["IsActive"] = true;
                if (!keywords) { row["MaxDepth"] = 2; row["Priority"] = 0; row["FollowExternal"] = false; }
                table.Rows.Add(row); await repository.SaveAsync(table.GetChanges()!);
                table = await repository.LoadAsync();
                if (table.Rows.Count != 1 || Convert.ToInt32(table.Rows[0]["Id"]) <= 0) throw new Exception("Insert failed");
                table.Rows[0][keywords ? "Keyword" : "Url"] = keywords ? "beta" : "https://example.com/changed";
                await repository.SaveAsync(table.GetChanges()!);
                var stale = await repository.LoadAsync();
                table = await repository.LoadAsync(); table.Rows[0]["IsActive"] = false;
                await repository.SaveAsync(table.GetChanges()!);
                stale.Rows[0]["IsActive"] = false;
                try { await repository.SaveAsync(stale.GetChanges()!); throw new Exception("Stale save accepted"); }
                catch (DBConcurrencyException) { Console.WriteLine("PASS: optimistic concurrency, keywords=" + keywords); }
                table = await repository.LoadAsync();
                if (keywords)
                {
                    await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
                    await Exec(connection, $"INSERT dbo.CrawlKeywordHits(Url,UrlHash,KeywordId,MatchCount) VALUES(N'https://example.com/',HASHBYTES('SHA2_256',N'https://example.com/'),{table.Rows[0]["Id"]},1)");
                }
                table.Rows[0].Delete(); await repository.SaveAsync(table.GetChanges()!);
                if ((await repository.LoadAsync()).Rows.Count != 0) throw new Exception("Delete failed");
                Console.WriteLine("PASS: add, update, delete with dependent cleanup, keywords=" + keywords);
            }
            Console.WriteLine("All SQL integration checks passed.");
        }
        finally
        {
            SqlConnection.ClearAllPools();
            if (created) await Exec(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        }
    }
}
