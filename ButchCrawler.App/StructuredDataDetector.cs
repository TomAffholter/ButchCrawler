namespace ButchCrawler.App;

public static class StructuredDataDetector
{
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".csv"] = "csv", [".tsv"] = "tsv", [".tab"] = "tsv",
        [".json"] = "json", [".jsonl"] = "jsonl", [".ndjson"] = "jsonl", [".geojson"] = "json",
        [".xml"] = "xml", [".sql"] = "sql",
        [".xlsx"] = "excel", [".xls"] = "access-excel",
        [".sqlite"] = "sqlite", [".sqlite3"] = "sqlite", [".db"] = "sqlite",
        [".mdb"] = "access", [".accdb"] = "access", [".dbf"] = "dbf",
        [".zip"] = "zip"
    };

    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text/csv"] = "csv", ["text/tab-separated-values"] = "tsv",
        ["application/json"] = "json", ["application/ld+json"] = "json", ["application/geo+json"] = "json",
        ["application/x-ndjson"] = "jsonl", ["application/xml"] = "xml", ["text/xml"] = "xml",
        ["application/sql"] = "sql", ["text/x-sql"] = "sql",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "excel",
        ["application/vnd.ms-excel"] = "access-excel",
        ["application/vnd.sqlite3"] = "sqlite", ["application/x-sqlite3"] = "sqlite",
        ["application/x-msaccess"] = "access", ["application/x-dbf"] = "dbf",
        ["application/zip"] = "zip", ["application/x-zip-compressed"] = "zip"
    };

    public static IReadOnlyCollection<string> SupportedExtensions => Extensions.Keys;

    public static string? Detect(string? contentType, string? extension, ReadOnlySpan<byte> header = default)
    {
        var cleanType = contentType?.Split(';', 2)[0].Trim();
        if (!string.IsNullOrWhiteSpace(cleanType) && MimeTypes.TryGetValue(cleanType, out var byMime)) return byMime;
        if (!string.IsNullOrWhiteSpace(extension))
        {
            var ext = extension.StartsWith('.') ? extension : "." + extension;
            if (Extensions.TryGetValue(ext, out var byExtension)) return byExtension;
        }
        if (header.Length >= 16 && header[..16].SequenceEqual("SQLite format 3\0"u8)) return "sqlite";
        if (header.Length >= 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] is 0x03 or 0x05 or 0x07 && header[3] is 0x04 or 0x06 or 0x08) return "zip";
        var trimmed = header;
        while (!trimmed.IsEmpty && char.IsWhiteSpace((char)trimmed[0])) trimmed = trimmed[1..];
        if (!trimmed.IsEmpty && trimmed[0] is (byte)'{' or (byte)'[') return "json";
        if (!trimmed.IsEmpty && trimmed[0] == (byte)'<') return "xml";
        return null;
    }

    public static bool IsStructuredLink(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (Detect(null, Path.GetExtension(uri.AbsolutePath)) != null) return true;
        var value = (uri.Host + uri.AbsolutePath + uri.Query).ToLowerInvariant();
        return uri.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("/api/") || value.Contains("/export") || value.Contains("/download") ||
            value.Contains("bulk") || value.Contains("dataset") || value.Contains("repository") ||
            value.Contains("format=json") || value.Contains("format=csv") || value.Contains("format=xml");
    }

    public static string Category(string format) => format switch
    {
        "sqlite" or "access" or "dbf" => "Database",
        "csv" or "tsv" or "excel" or "access-excel" => "Table",
        "json" or "jsonl" or "xml" => "Repository API / structured export",
        "sql" => "SQL text archive (never executed)",
        "zip" => "Bulk archive",
        _ => "Structured data"
    };
}
