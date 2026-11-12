using System.IO.Compression;
using System.IO;
using System.Text;
using ButchCrawler.App;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class StructuredDataTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StructuredData:Enabled"] = "true", ["StructuredData:MaxDownloadBytes"] = "1000000",
            ["StructuredData:MaxRowsPerTable"] = "100", ["StructuredData:MaxTables"] = "20",
            ["StructuredData:MaxArchiveEntries"] = "20", ["StructuredData:MaxArchiveExpandedBytes"] = "2000000",
            ["StructuredData:MaxArchiveCompressionRatio"] = "1000"
        }).Build();
        var processor = new StructuredDataProcessor(config, NullLogger<StructuredDataProcessor>.Instance);

        check(StructuredDataDetector.Detect("application/vnd.github+json", null) == null &&
              StructuredDataDetector.Detect("application/json", null) == "json" &&
              StructuredDataDetector.IsStructuredLink("https://archives.example/api/records?format=json"),
            "repository API and export links are recognized without inventing endpoints");

        var csv = await Process(processor, "https://archives.example/export.csv", "Name,Place,Note\r\n\"Cassidy, Butch\",Utah,\"Line one\nline two\"", "text/csv", ".csv");
        check(csv.Text.Contains("Name=Cassidy, Butch") && csv.Text.Contains("Place=Utah") && csv.StructuredRecordCount == 1,
            "CSV quoted commas and multiline cells become searchable rows");

        var json = await Process(processor, "https://api.example/records", "{\"people\":[{\"name\":\"Etta Place\",\"year\":1901}]}", "application/json", null);
        check(json.Text.Contains("Etta Place") && json.Text.Contains("year = 1901"), "JSON repository values and paths become searchable");

        var xml = await Process(processor, "https://archive.example/export.xml", "<records><record id=\"7\"><name>Sundance Kid</name></record></records>", "application/xml", ".xml");
        check(xml.Text.Contains("/@id = 7") && xml.Text.Contains("Sundance Kid"), "XML values and attributes become searchable with DTDs disabled");
        try
        {
            await Process(processor, "bad.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><x>&e;</x>", "application/xml", ".xml");
            throw new Exception("Expected XML DTD rejection.");
        }
        catch (System.Xml.XmlException) { check(true, "XML external-entity input is rejected"); }

        var sql = await Process(processor, "dump.sql", "CREATE TABLE people(name text);\nINSERT INTO people VALUES('Harvey Logan');\npassword=SecretValue", "text/sql", ".sql");
        check(sql.Text.Contains("Harvey Logan") && sql.Text.Contains("[REDACTED]") && !sql.Text.Contains("SecretValue"),
            "SQL dumps are indexed as redacted text and never executed");

        var excel = await processor.ProcessAsync("register.xlsx", CreateXlsx(), null, ".xlsx", default);
        check(excel.Text.Contains("Wyoming") && excel.Text.Contains("A1=Name"), "modern Excel worksheets become searchable without running formulas or macros");

        var sqlite = await processor.ProcessAsync("catalog.sqlite", await CreateSqliteAsync(), null, ".sqlite", default);
        check(sqlite.Text.Contains("Kid Curry") && sqlite.Text.Contains("place=Montana"), "SQLite user tables are opened read-only and indexed");

        var dbf = await processor.ProcessAsync("people.dbf", CreateDbf(), null, ".dbf", default);
        check(dbf.Text.Contains("NAME=Matt Warner"), "DBF records become searchable without a database driver");

        var zip = await processor.ProcessAsync("bulk.zip", CreateZip(), "application/zip", ".zip", default);
        check(zip.Text.Contains("Fort Worth") && zip.StructuredEntryCount == 2 && !zip.Text.Contains("should-not-run"),
            "ZIP bulk exports index only supported entries and never execute embedded programs");

        var limitedConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["StructuredData:Enabled"] = "true", ["StructuredData:MaxDownloadBytes"] = "10" }).Build();
        var limited = new StructuredDataProcessor(limitedConfig, NullLogger<StructuredDataProcessor>.Instance);
        try { await limited.ProcessAsync("large.csv", new byte[11], "text/csv", ".csv", default); throw new Exception("Expected size rejection."); }
        catch (InvalidDataException) { check(true, "structured downloads obey their independent size limit"); }
    }

    private static Task<ProcessedPayload> Process(StructuredDataProcessor processor, string source, string text, string? mime, string? extension) =>
        processor.ProcessAsync(source, Encoding.UTF8.GetBytes(text), mime, extension, default);

    private static byte[] CreateXlsx()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbook = document.AddWorkbookPart(); workbook.Workbook = new Workbook();
            var worksheet = workbook.AddNewPart<WorksheetPart>(); worksheet.Worksheet = new Worksheet(new SheetData(
                new Row(new Cell { CellReference = "A1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Name")) },
                        new Cell { CellReference = "B1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Place")) }),
                new Row(new Cell { CellReference = "A2", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Tom Horn")) },
                        new Cell { CellReference = "B2", DataType = CellValues.InlineString, InlineString = new InlineString(new Text("Wyoming")) })));
            var sheets = workbook.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "People" }); workbook.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static async Task<byte[]> CreateSqliteAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "HistoryCrawlerTest-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            await using (var connection = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                await connection.OpenAsync(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE people(name TEXT,place TEXT); INSERT INTO people VALUES('Kid Curry','Montana');";
                await command.ExecuteNonQueryAsync();
            }
            return await File.ReadAllBytesAsync(path);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static byte[] CreateDbf()
    {
        var bytes = new byte[77]; bytes[0] = 0x03; BitConverter.GetBytes((uint)1).CopyTo(bytes, 4);
        BitConverter.GetBytes((ushort)65).CopyTo(bytes, 8); BitConverter.GetBytes((ushort)12).CopyTo(bytes, 10);
        Encoding.ASCII.GetBytes("NAME").CopyTo(bytes, 32); bytes[43] = (byte)'C'; bytes[48] = 11; bytes[64] = 0x0D;
        bytes[65] = (byte)' '; Encoding.ASCII.GetBytes("Matt Warner").CopyTo(bytes, 66); return bytes;
    }

    private static byte[] CreateZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Write(archive, "records.csv", "Name,Place\nBassett,Fort Worth");
            Write(archive, "metadata.json", "{\"collection\":\"Wild Bunch\"}");
            Write(archive, "program.exe", "should-not-run");
        }
        return stream.ToArray();
        static void Write(ZipArchive archive, string name, string value)
        { using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8); writer.Write(value); }
    }
}
