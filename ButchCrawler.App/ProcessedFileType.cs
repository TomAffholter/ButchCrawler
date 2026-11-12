namespace ButchCrawler.App;

public static class ProcessedFileType
{
    public static string Label(string? contentType, string? extension, string? structuredFormat = null)
    {
        var ext = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension.Trim().TrimStart('.').ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(structuredFormat))
        {
            return structuredFormat.ToLowerInvariant() switch
            {
                "access" => ext is "MDB" or "ACCDB" ? ext : "ACCESS",
                "access-excel" => "XLS",
                "excel" => "XLSX",
                "sqlite" => "SQLITE",
                "csv" => "CSV", "tsv" => "TSV", "json" => "JSON", "jsonl" => "JSONL",
                "xml" => "XML", "sql" => "SQL", "dbf" => "DBF", "zip" => "ZIP",
                _ => SafeLabel(structuredFormat)
            };
        }
        if (contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true || ext is "HTML" or "HTM") return "HTML";
        if (contentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true || ext == "PDF") return "PDF";
        if (contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true || ext is "JPG" or "JPEG" or "PNG" or "BMP" or "TIF" or "TIFF" or "WEBP")
            return ext switch { "JPG" or "JPEG" => "JPEG", "TIF" or "TIFF" => "TIFF", "" => "IMAGE", _ => ext };
        if (contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true || AudioProcessor.Extensions.Contains("." + ext, StringComparer.OrdinalIgnoreCase)) return "AUDIO";
        return ext switch { "MHTML" => "MHT", "" => "OTHER", _ => ext };
    }

    private static string SafeLabel(string value)
    {
        var label = new string(value.Where(char.IsLetterOrDigit).Take(12).ToArray()).ToUpperInvariant();
        return label.Length == 0 ? "OTHER" : label;
    }
}
