using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App;

/// <summary>
/// Converts downloadable datasets into bounded, searchable plain text. It never executes
/// SQL from a source, never enables SQLite extensions, and never extracts archives to a
/// user-controlled directory.
/// </summary>
public sealed class StructuredDataProcessor : IPayloadProcessor
{
    private readonly ILogger<StructuredDataProcessor> _logger;
    private readonly bool _enabled;
    private readonly long _maxDownloadBytes;
    private readonly long _maxExpandedBytes;
    private readonly int _maxArchiveEntries;
    private readonly int _maxArchiveDepth;
    private readonly int _maxCompressionRatio;
    private readonly int _maxRows;
    private readonly int _maxTables;
    private readonly int _maxColumns;
    private readonly int _maxCellCharacters;
    private readonly int _maxExtractedCharacters;

    public StructuredDataProcessor(IConfiguration config, ILogger<StructuredDataProcessor> logger)
    {
        _logger = logger;
        _enabled = config.GetValue("StructuredData:Enabled", true);
        _maxDownloadBytes = Positive(config.GetValue<long>("StructuredData:MaxDownloadBytes", 50_000_000), 50_000_000);
        _maxExpandedBytes = Positive(config.GetValue<long>("StructuredData:MaxArchiveExpandedBytes", 100_000_000), 100_000_000);
        _maxArchiveEntries = Clamp(config.GetValue("StructuredData:MaxArchiveEntries", 200), 1, 10_000);
        _maxArchiveDepth = Clamp(config.GetValue("StructuredData:MaxArchiveDepth", 2), 0, 5);
        _maxCompressionRatio = Clamp(config.GetValue("StructuredData:MaxArchiveCompressionRatio", 100), 2, 10_000);
        _maxRows = Clamp(config.GetValue("StructuredData:MaxRowsPerTable", 5_000), 1, 100_000);
        _maxTables = Clamp(config.GetValue("StructuredData:MaxTables", 100), 1, 1_000);
        _maxColumns = Clamp(config.GetValue("StructuredData:MaxColumns", 200), 1, 2_000);
        _maxCellCharacters = Clamp(config.GetValue("StructuredData:MaxCellCharacters", 4_000), 32, 100_000);
        _maxExtractedCharacters = Clamp(config.GetValue("StructuredData:MaxExtractedCharacters", 20_000_000), 1_000, 100_000_000);
    }

    public async Task<ProcessedPayload> ProcessAsync(string url, byte[] body, string? contentType, string? extension, CancellationToken ct)
    {
        if (!_enabled) throw new InvalidDataException("Structured-data discovery is disabled in Settings.");
        if (body.Length == 0) throw new InvalidDataException("The dataset is empty.");
        if (body.LongLength > _maxDownloadBytes) throw new InvalidDataException($"Dataset exceeds the {_maxDownloadBytes:N0}-byte safety limit.");
        var format = StructuredDataDetector.Detect(contentType, extension, body.AsSpan(0, Math.Min(body.Length, 32)))
            ?? throw new InvalidDataException("This structured-data format is not supported.");
        var state = new ExtractionState(_maxExtractedCharacters);
        await ExtractAsync(url, body, format, state, 0, ct).ConfigureAwait(false);
        if (state.Builder.Length == 0) throw new InvalidDataException("No searchable values were found in this dataset.");

        var warning = state.Warnings.Count == 0 ? null : string.Join(" ", state.Warnings.Distinct(StringComparer.Ordinal));
        var prefix = new StringBuilder()
            .AppendLine("[Structured dataset]")
            .AppendLine($"Source: {url}")
            .AppendLine($"Format: {format}")
            .AppendLine($"Category: {StructuredDataDetector.Category(format)}")
            .AppendLine($"Records indexed: {state.Records:N0}")
            .AppendLine($"Tables/sheets: {state.Tables:N0}")
            .AppendLine($"Archive entries: {state.Entries:N0}");
        if (state.Truncated) prefix.AppendLine("Notice: The searchable preview was safely limited; the source may contain additional records.");
        if (!string.IsNullOrWhiteSpace(warning)) prefix.AppendLine("Notice: " + warning);
        prefix.AppendLine();
        prefix.Append(state.Builder);
        var text = prefix.Length > _maxExtractedCharacters ? prefix.ToString(0, _maxExtractedCharacters) : prefix.ToString();

        return new ProcessedPayload
        {
            Text = text,
            ContentHash = SHA256.HashData(body),
            Title = $"{StructuredDataDetector.Category(format)} ({format})",
            StructuredFormat = format,
            StructuredCategory = StructuredDataDetector.Category(format),
            StructuredRecordCount = state.Records,
            StructuredTableCount = state.Tables,
            StructuredEntryCount = state.Entries,
            StructuredTruncated = state.Truncated || prefix.Length > _maxExtractedCharacters,
            StructuredWarning = warning
        };
    }

    private async Task ExtractAsync(string source, byte[] body, string format, ExtractionState state, int archiveDepth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (format)
        {
            case "csv": ExtractDelimited(source, body, ',', state, ct); break;
            case "tsv": ExtractDelimited(source, body, '\t', state, ct); break;
            case "json": ExtractJson(source, body, state, ct); break;
            case "jsonl": ExtractJsonLines(source, body, state, ct); break;
            case "xml": ExtractXml(source, body, state, ct); break;
            case "sql": ExtractSql(source, body, state, ct); break;
            case "excel": ExtractXlsx(source, body, state, ct); break;
            case "sqlite": await ExtractSqliteAsync(source, body, state, ct).ConfigureAwait(false); break;
            case "access":
            case "access-excel": await ExtractOleDbAsync(source, body, format, state, ct).ConfigureAwait(false); break;
            case "dbf": ExtractDbf(source, body, state, ct); break;
            case "zip": await ExtractZipAsync(source, body, state, archiveDepth, ct).ConfigureAwait(false); break;
            default: throw new InvalidDataException($"Unsupported structured-data format: {format}");
        }
    }

    private void ExtractDelimited(string source, byte[] body, char delimiter, ExtractionState state, CancellationToken ct)
    {
        using var reader = new StringReader(DecodeText(body));
        var headers = ReadDelimitedRecord(reader, delimiter)?.Take(_maxColumns).Select((x, i) => CleanHeader(x, i)).ToArray();
        if (headers is null || headers.Length == 0) return;
        state.Tables++;
        state.AppendLine($"[Table: {DisplayName(source)}]");
        string[]? row;
        var count = 0;
        while (count < _maxRows && (row = ReadDelimitedRecord(reader, delimiter)) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            AppendRow(state, ++count, headers, row);
        }
        if (reader.Peek() >= 0) state.Truncate("Row limit reached.");
    }

    private static string[]? ReadDelimitedRecord(TextReader reader, char delimiter)
    {
        var cells = new List<string>(); var cell = new StringBuilder(); var quoted = false; var readAny = false;
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                if (!readAny && cell.Length == 0 && cells.Count == 0) return null;
                cells.Add(cell.ToString()); return cells.ToArray();
            }
            readAny = true; var ch = (char)next;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); cell.Append('"'); }
                    else quoted = false;
                }
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == delimiter) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { if (reader.Peek() == '\n') reader.Read(); cells.Add(cell.ToString()); return cells.ToArray(); }
            else if (ch == '\n') { cells.Add(cell.ToString()); return cells.ToArray(); }
            else cell.Append(ch);
        }
    }

    private void ExtractJson(string source, byte[] body, ExtractionState state, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        state.Tables++;
        state.AppendLine($"[JSON repository/export: {DisplayName(source)}]");
        var records = 0;
        WalkJson(document.RootElement, "$", state, ref records, ct);
    }

    private void WalkJson(JsonElement element, string path, ExtractionState state, ref int records, CancellationToken ct)
    {
        if (state.Truncated || records >= _maxRows) { state.Truncate("JSON record limit reached."); return; }
        ct.ThrowIfCancellationRequested();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                records++; state.Records++;
                foreach (var property in element.EnumerateObject().Take(_maxColumns))
                    WalkJson(property.Value, path + "." + property.Name, state, ref records, ct);
                break;
            case JsonValueKind.Array:
                var i = 0; foreach (var item in element.EnumerateArray()) { WalkJson(item, path + "[" + i++ + "]", state, ref records, ct); if (state.Truncated) break; }
                break;
            case JsonValueKind.String: state.AppendLine(path + " = " + Cell(element.GetString())); break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False: state.AppendLine(path + " = " + Cell(element.GetRawText())); break;
            case JsonValueKind.Null: state.AppendLine(path + " = null"); break;
        }
    }

    private void ExtractJsonLines(string source, byte[] body, ExtractionState state, CancellationToken ct)
    {
        state.Tables++; state.AppendLine($"[JSON Lines export: {DisplayName(source)}]");
        using var reader = new StringReader(DecodeText(body)); string? line; var row = 0;
        while (row < _maxRows && (line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
            var records = 0; state.AppendLine($"Row {++row}"); WalkJson(document.RootElement, "$", state, ref records, ct);
        }
        if (reader.Peek() >= 0) state.Truncate("JSON Lines row limit reached.");
    }

    private void ExtractXml(string source, byte[] body, ExtractionState state, CancellationToken ct)
    {
        state.Tables++; state.AppendLine($"[XML repository/export: {DisplayName(source)}]");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = _maxDownloadBytes, IgnoreComments = true };
        using var input = new MemoryStream(body, writable: false); using var reader = XmlReader.Create(input, settings);
        var path = new Stack<string>(); var records = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                path.Push(reader.LocalName); if (path.Count == 2) { records++; state.Records++; if (records > _maxRows) { state.Truncate("XML element limit reached."); break; } }
                if (reader.HasAttributes)
                {
                    var shown = 0; while (reader.MoveToNextAttribute() && shown++ < _maxColumns)
                        state.AppendLine(XmlPath(path) + "/@" + reader.LocalName + " = " + Cell(reader.Value));
                    reader.MoveToElement();
                }
                if (reader.IsEmptyElement) path.Pop();
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !string.IsNullOrWhiteSpace(reader.Value))
                state.AppendLine(XmlPath(path) + " = " + Cell(reader.Value));
            else if (reader.NodeType == XmlNodeType.EndElement && path.Count > 0) path.Pop();
        }
    }

    private void ExtractSql(string source, byte[] body, ExtractionState state, CancellationToken ct)
    {
        state.Tables++; state.AppendLine($"[SQL text archive: {DisplayName(source)}]");
        state.AppendLine("Safety: stored as text only; no statement was executed.");
        using var reader = new StringReader(DecodeText(body)); string? line; var lines = 0;
        while (lines < _maxRows * 5 && (line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            state.AppendLine(RedactLikelySecret(Cell(line)));
            lines++; if (line.Contains("INSERT", StringComparison.OrdinalIgnoreCase)) state.Records++;
        }
        if (reader.Peek() >= 0) state.Truncate("SQL text line limit reached.");
    }

    private void ExtractXlsx(string source, byte[] body, ExtractionState state, CancellationToken ct)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var workbook = SpreadsheetDocument.Open(stream, false, new OpenSettings { AutoSave = false, MaxCharactersInPart = _maxExtractedCharacters });
        var part = workbook.WorkbookPart ?? throw new InvalidDataException("Excel workbook has no workbook part.");
        var shared = part.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? Array.Empty<string>();
        var workbookRoot = part.Workbook ?? throw new InvalidDataException("Excel workbook is incomplete.");
        foreach (var sheet in workbookRoot.Sheets?.Elements<Sheet>().Take(_maxTables) ?? Enumerable.Empty<Sheet>())
        {
            ct.ThrowIfCancellationRequested();
            if (sheet.Id?.Value == null || part.GetPartById(sheet.Id.Value) is not WorksheetPart worksheet) continue;
            state.Tables++; state.AppendLine($"[Sheet: {Cell(sheet.Name?.Value ?? "Sheet")}]");
            var worksheetRoot = worksheet.Worksheet ?? throw new InvalidDataException("Excel worksheet is incomplete.");
            var rows = worksheetRoot.GetFirstChild<SheetData>()?.Elements<Row>().Take(_maxRows + 1).ToArray() ?? Array.Empty<Row>();
            foreach (var row in rows.Take(_maxRows))
            {
                ct.ThrowIfCancellationRequested(); state.Records++;
                var values = row.Elements<Cell>().Take(_maxColumns).Select(c => ExcelValue(c, shared));
                state.AppendLine($"Row {row.RowIndex?.Value ?? (uint)state.Records} | " + string.Join(" | ", values.Where(x => x.Length > 0)));
            }
            if (rows.Length > _maxRows) state.Truncate("Excel row limit reached.");
        }
    }

    private static string ExcelValue(Cell cell, string[] shared)
    {
        var value = cell.CellValue?.InnerText ?? cell.InlineString?.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(value, out var index) && index >= 0 && index < shared.Length) value = shared[index];
        return (cell.CellReference?.Value ?? "Cell") + "=" + value;
    }

    private async Task ExtractSqliteAsync(string source, byte[] body, ExtractionState state, CancellationToken ct)
    {
        var path = TempPath(".sqlite"); await File.WriteAllBytesAsync(path, body, ct).ConfigureAwait(false);
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false };
            await using var connection = new SqliteConnection(builder.ConnectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
            using (var queryOnly = connection.CreateCommand()) { queryOnly.CommandText = "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;"; await queryOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
            var tables = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name LIMIT @max";
                command.Parameters.AddWithValue("@max", _maxTables + 1);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false)) tables.Add(reader.GetString(0));
            }
            if (tables.Count > _maxTables) { tables.RemoveAt(tables.Count - 1); state.Truncate("SQLite table limit reached."); }
            foreach (var table in tables)
            {
                ct.ThrowIfCancellationRequested(); state.Tables++; state.AppendLine($"[SQLite table: {Cell(table)}]");
                using var command = connection.CreateCommand(); command.CommandText = $"SELECT * FROM {QuoteSqlite(table)} LIMIT {_maxRows + 1}";
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false); var row = 0;
                while (row <= _maxRows && await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (row++ == _maxRows) { state.Truncate($"SQLite row limit reached for {table}."); break; }
                    var values = new List<string>();
                    for (var i = 0; i < Math.Min(reader.FieldCount, _maxColumns); i++) values.Add(reader.GetName(i) + "=" + DbValue(reader.GetValue(i)));
                    state.Records++; state.AppendLine($"Row {row} | " + string.Join(" | ", values));
                }
            }
        }
        finally { TryDelete(path); }
    }

    private async Task ExtractOleDbAsync(string source, byte[] body, string format, ExtractionState state, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Access and legacy Excel import require Windows and the Microsoft Access Database Engine.");
        var extension = format == "access-excel" ? ".xls" : Path.GetExtension(source).Equals(".mdb", StringComparison.OrdinalIgnoreCase) ? ".mdb" : ".accdb";
        var path = TempPath(extension); await File.WriteAllBytesAsync(path, body, ct).ConfigureAwait(false);
        try
        {
            var providers = format == "access-excel"
                ? new[] { $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};Extended Properties=\"Excel 8.0;HDR=YES;IMEX=1\";Mode=Read;" }
                : new[] { $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};Mode=Read;", $"Provider=Microsoft.Jet.OLEDB.4.0;Data Source={path};Mode=Read;" };
            Exception? last = null;
            foreach (var connectionString in providers)
            {
                try { await ReadOleDbAsync(connectionString, state, ct).ConfigureAwait(false); return; }
                catch (Exception ex) when (ex is OleDbException or InvalidOperationException) { last = ex; }
            }
            throw new InvalidDataException("This file was detected, but reading it requires the 64-bit Microsoft Access Database Engine. The file was not executed or modified.", last);
        }
        finally { TryDelete(path); }
    }

    [SupportedOSPlatform("windows")]
    private async Task ReadOleDbAsync(string connectionString, ExtractionState state, CancellationToken ct)
    {
        await using var connection = new OleDbConnection(connectionString); await connection.OpenAsync(ct).ConfigureAwait(false);
        var schema = connection.GetSchema("Tables");
        var tables = schema.Rows.Cast<DataRow>()
            .Where(r => string.Equals(Convert.ToString(r["TABLE_TYPE"]), "TABLE", StringComparison.OrdinalIgnoreCase))
            .Select(r => Convert.ToString(r["TABLE_NAME"]) ?? string.Empty).Where(x => x.Length > 0).Take(_maxTables + 1).ToArray();
        if (tables.Length > _maxTables) state.Truncate("Access/Excel table limit reached.");
        foreach (var table in tables.Take(_maxTables))
        {
            ct.ThrowIfCancellationRequested(); state.Tables++; state.AppendLine($"[Access/Excel table: {Cell(table)}]");
            using var command = connection.CreateCommand(); command.CommandText = $"SELECT TOP {_maxRows + 1} * FROM [{table.Replace("]", "]]", StringComparison.Ordinal)}]";
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false); if (reader == null) continue;
            var row = 0; while (row <= _maxRows && await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (row++ == _maxRows) { state.Truncate($"Access/Excel row limit reached for {table}."); break; }
                var values = new List<string>();
                for (var i = 0; i < Math.Min(reader.FieldCount, _maxColumns); i++) values.Add(reader.GetName(i) + "=" + DbValue(reader.GetValue(i)));
                state.Records++; state.AppendLine($"Row {row} | " + string.Join(" | ", values));
            }
        }
    }

    private void ExtractDbf(string source, byte[] body, ExtractionState state, CancellationToken ct)
    {
        if (body.Length < 33) throw new InvalidDataException("DBF header is incomplete.");
        var records = BitConverter.ToUInt32(body, 4); var headerLength = BitConverter.ToUInt16(body, 8); var recordLength = BitConverter.ToUInt16(body, 10);
        if (headerLength < 33 || recordLength < 2 || headerLength > body.Length) throw new InvalidDataException("DBF header contains unsafe lengths.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); var encoding = Encoding.GetEncoding(1252);
        var fields = new List<(string Name, int Length)>();
        for (var offset = 32; offset + 32 <= headerLength && body[offset] != 0x0D && fields.Count < _maxColumns; offset += 32)
        {
            var name = encoding.GetString(body, offset, 11).TrimEnd('\0', ' '); var length = body[offset + 16];
            if (length > 0) fields.Add((name.Length == 0 ? "Column" + (fields.Count + 1) : name, length));
        }
        if (fields.Sum(x => x.Length) + 1 > recordLength) throw new InvalidDataException("DBF field widths exceed its record width.");
        state.Tables++; state.AppendLine($"[DBF table: {DisplayName(source)}]");
        var max = (int)Math.Min(records, (uint)_maxRows);
        for (var row = 0; row < max; row++)
        {
            ct.ThrowIfCancellationRequested(); var offset = headerLength + row * recordLength; if (offset + recordLength > body.Length) { state.Warnings.Add("DBF ended before its declared record count."); break; }
            if (body[offset] == (byte)'*') continue;
            var position = offset + 1; var values = new List<string>();
            foreach (var field in fields) { values.Add(field.Name + "=" + Cell(encoding.GetString(body, position, field.Length).Trim())); position += field.Length; }
            state.Records++; state.AppendLine($"Row {row + 1} | " + string.Join(" | ", values));
        }
        if (records > _maxRows) state.Truncate("DBF row limit reached.");
    }

    private async Task ExtractZipAsync(string source, byte[] body, ExtractionState state, int depth, CancellationToken ct)
    {
        if (depth > _maxArchiveDepth) { state.Truncate("Nested archive depth limit reached."); return; }
        using var stream = new MemoryStream(body, writable: false); using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        long expanded = 0; var considered = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested(); if (string.IsNullOrEmpty(entry.Name)) continue;
            if (++considered > _maxArchiveEntries) { state.Truncate("Archive entry-count limit reached."); break; }
            if (entry.Length < 0 || entry.Length > _maxDownloadBytes || expanded + entry.Length > _maxExpandedBytes)
            { state.Warnings.Add($"Skipped oversized archive entry {entry.Name}."); state.Truncated = true; continue; }
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > _maxCompressionRatio)
            { state.Warnings.Add($"Skipped unusually compressed archive entry {entry.Name}."); state.Truncated = true; continue; }
            if (++considered > _maxArchiveEntries) { state.Truncate("Archive entry limit reached."); break; }
            if (entry.Length < 0 || entry.Length > _maxDownloadBytes || expanded + entry.Length > _maxExpandedBytes) { state.Warnings.Add($"Skipped oversized archive entry {entry.Name}."); continue; }
            var format = StructuredDataDetector.Detect(null, Path.GetExtension(entry.Name)); if (format == null) continue;
            if (format == "zip" && depth >= _maxArchiveDepth) { state.Warnings.Add($"Skipped deeply nested archive {entry.Name}."); continue; }
            await using var input = entry.Open(); using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            await CopyBoundedAsync(input, output, _maxDownloadBytes, ct).ConfigureAwait(false); expanded += output.Length; state.Entries++;
            state.AppendLine($"[Archive entry: {Cell(entry.FullName)}]");
            try { await ExtractAsync(source + "!" + entry.FullName, output.ToArray(), format, state, depth + 1, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or XmlException) { state.Warnings.Add($"Could not read {entry.Name}: {ex.Message}"); }
            if (state.Truncated && state.Builder.Length >= _maxExtractedCharacters) break;
        }
        if (state.Entries == 0) state.Warnings.Add("The archive contained no supported structured-data entries.");
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long max, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        { total += read; if (total > max) throw new InvalidDataException("Archive entry expanded beyond its safety limit."); await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
    }

    private void AppendRow(ExtractionState state, int number, IReadOnlyList<string> headers, IReadOnlyList<string> row)
    {
        state.Records++; var values = new List<string>();
        for (var i = 0; i < Math.Min(Math.Min(headers.Count, row.Count), _maxColumns); i++) values.Add(headers[i] + "=" + Cell(row[i]));
        state.AppendLine($"Row {number} | " + string.Join(" | ", values));
    }

    private string Cell(string? value)
    {
        var clean = Regex.Replace(value ?? string.Empty, @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", " ").Trim();
        return clean.Length <= _maxCellCharacters ? clean : clean[.._maxCellCharacters] + "…";
    }

    private static string DbValue(object value) => value switch
    { DBNull => "null", byte[] bytes => $"[binary {bytes.Length:N0} bytes]", DateTime date => date.ToString("O", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty };
    private static string QuoteSqlite(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string CleanHeader(string value, int index) => string.IsNullOrWhiteSpace(value) ? "Column" + (index + 1) : value.Trim();
    private static string DisplayName(string source) { try { return Path.GetFileName(new Uri(source, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(source).AbsolutePath : source); } catch { return source; } }
    private static string XmlPath(IEnumerable<string> path) => "/" + string.Join("/", path.Reverse());
    private static string DecodeText(byte[] body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) return Encoding.UTF8.GetString(body, 3, body.Length - 3);
        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE) return Encoding.Unicode.GetString(body, 2, body.Length - 2);
        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2);
        return new UTF8Encoding(false, false).GetString(body);
    }
    private static string RedactLikelySecret(string line) => Regex.Replace(line,
        "(?i)(password|passwd|pwd|api[_-]?key|secret|access[_-]?token)(\\s*[:=]\\s*|\\s+)(['\\\"]?)[^,'\\\";\\s)]+",
        "$1$2$3[REDACTED]");
    private static string TempPath(string extension) => Path.Combine(Path.GetTempPath(), "HistoryCrawler-" + Guid.NewGuid().ToString("N") + extension);
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static long Positive(long value, long fallback) => value > 0 ? value : fallback;
    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private sealed class ExtractionState(int maxCharacters)
    {
        public StringBuilder Builder { get; } = new();
        public long Records { get; set; }
        public int Tables { get; set; }
        public int Entries { get; set; }
        public bool Truncated { get; set; }
        public List<string> Warnings { get; } = new();
        public void AppendLine(string value)
        {
            if (Builder.Length >= maxCharacters) { Truncated = true; return; }
            var remaining = maxCharacters - Builder.Length;
            if (value.Length + Environment.NewLine.Length > remaining) { Builder.Append(value.AsSpan(0, Math.Max(0, remaining - 1))); Builder.Append('…'); Truncated = true; }
            else Builder.AppendLine(value);
        }
        public void Truncate(string warning) { Truncated = true; Warnings.Add(warning); }
    }
}
