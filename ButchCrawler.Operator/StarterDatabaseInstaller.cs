using System.Data;
using System.IO;
using Microsoft.Data.SqlClient;

namespace ButchCrawler.Operator;

public sealed record StarterDatabaseInstallStatus(bool Ready, bool CanInstall, string Message);

/// <summary>
/// Installs the bundled clean starter backup only when the configured database
/// does not already exist. Existing databases are never overwritten.
/// </summary>
public sealed class StarterDatabaseInstaller(string connectionString, string backupPath)
{
    public async Task<StarterDatabaseInstallStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var database = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(database)) return new(false, false, "No database name is configured.");
        builder.InitialCatalog = "master";
        builder.Pooling = false;
        try
        {
            await using var master = new SqlConnection(builder.ConnectionString);
            await master.OpenAsync(cancellationToken);
            using var command = master.CreateCommand();
            command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = database;
            command.CommandText = "SELECT CASE WHEN DB_ID(@database) IS NULL THEN 0 ELSE 1 END";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                await ValidateExistingAsync(connectionString, cancellationToken);
                return new(true, false, "Ready: " + database);
            }
            return File.Exists(backupPath)
                ? new(false, true, "The clean starter database will be installed when setup finishes.")
                : new(false, false, "Database is missing and the installer does not contain HistoryCrawlerStarter.bak.");
        }
        catch (Exception ex)
        {
            return new(false, false, "SQL Server is not ready: " + ex.Message);
        }
    }

    public async Task<bool> InstallIfMissingAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var targetBuilder = new SqlConnectionStringBuilder(connectionString);
        var database = targetBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(database)) throw new InvalidOperationException("No database name is configured.");
        var masterBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false };
        await using var master = new SqlConnection(masterBuilder.ConnectionString);
        await master.OpenAsync(cancellationToken);
        using (var exists = master.CreateCommand())
        {
            exists.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = database;
            exists.CommandText = "SELECT CASE WHEN DB_ID(@database) IS NULL THEN 0 ELSE 1 END";
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                await ValidateExistingAsync(connectionString, cancellationToken);
                return false;
            }
        }

        if (!File.Exists(backupPath))
            throw new FileNotFoundException("The clean starter database was not included with this installation.", backupPath);

        progress?.Report("Installing the clean starter database…");
        var files = new List<(string Logical, byte Type)>();
        using (var list = master.CreateCommand())
        {
            list.CommandTimeout = 300;
            list.CommandText = $"RESTORE FILELISTONLY FROM DISK=N'{SqlLiteral(backupPath)}';";
            using var reader = await list.ExecuteReaderAsync(cancellationToken);
            var nameOrdinal = reader.GetOrdinal("LogicalName");
            var typeOrdinal = reader.GetOrdinal("Type");
            while (await reader.ReadAsync(cancellationToken))
            {
                var typeValue = reader.GetValue(typeOrdinal);
                var type = typeValue is byte value ? value : Convert.ToByte(Convert.ToChar(typeValue));
                files.Add((reader.GetString(nameOrdinal), type));
            }
        }

        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HistoryCrawler", "Database");
        Directory.CreateDirectory(dataDirectory);
        var dataNumber = 0;
        var logNumber = 0;
        var moves = new List<string>();
        foreach (var file in files)
        {
            var isLog = file.Type == (byte)'L';
            var number = isLog ? ++logNumber : ++dataNumber;
            var filename = isLog
                ? (number == 1 ? "HistoryCrawler.ldf" : $"HistoryCrawler_log{number}.ldf")
                : (number == 1 ? "HistoryCrawler.mdf" : $"HistoryCrawler_data{number}.ndf");
            moves.Add($"MOVE N'{SqlLiteral(file.Logical)}' TO N'{SqlLiteral(Path.Combine(dataDirectory, filename))}'");
        }
        if (dataNumber == 0 || logNumber == 0)
            throw new InvalidDataException("The starter backup did not contain both a data file and a log file.");

        using (var restore = master.CreateCommand())
        {
            restore.CommandTimeout = 1200;
            restore.CommandText = $"RESTORE DATABASE {QuoteIdentifier(database)} FROM DISK=N'{SqlLiteral(backupPath)}' WITH {string.Join(", ", moves)}, RECOVERY, STATS=10;";
            await restore.ExecuteNonQueryAsync(cancellationToken);
        }
        SqlConnection.ClearAllPools();
        await ValidateExistingAsync(connectionString, cancellationToken);
        progress?.Report("Clean starter database installed.");
        return true;
    }

    private static async Task ValidateExistingAsync(string targetConnectionString, CancellationToken cancellationToken)
    {
        await using var target = new SqlConnection(targetConnectionString);
        await target.OpenAsync(cancellationToken);
        using var command = target.CreateCommand();
        command.CommandText = @"SELECT CASE WHEN OBJECT_ID(N'dbo.SeedUrls',N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.SeedKeywords',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.SchemaVersion',N'U') IS NOT NULL
 THEN 1 ELSE 0 END";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new InvalidOperationException("The existing database is not a complete History Crawler database. It was not changed.");
    }

    private static string QuoteIdentifier(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
