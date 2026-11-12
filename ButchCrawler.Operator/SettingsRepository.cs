using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ButchCrawler.Operator;

public sealed class SettingRow
{
    public string Property { get; }
    public string Value { get; set; }
    internal string Original { get; }
    internal string[] Path { get; }
    internal JsonValueKind Kind { get; }
    internal SettingRow(string[] path, JsonNode? value)
    {
        Path = path; Property = string.Join(" : ", path);
        Kind = value?.GetValueKind() ?? JsonValueKind.Null;
        Value = Kind == JsonValueKind.String ? value!.GetValue<string>() : value?.ToJsonString() ?? "null";
        Original = Value;
    }
}

public sealed class SettingsRepository
{
    private readonly string _path;
    private readonly string _snapshot;
    private readonly List<SettingRow> _rows = new();
    public IReadOnlyList<SettingRow> Rows => _rows.AsReadOnly();
    public bool HasChanges => _rows.Any(row => row.Value != row.Original);

    public SettingsRepository(string path)
    {
        _path = path; _snapshot = File.ReadAllText(path);
        var root = Parse(_snapshot);
        Collect(root, Array.Empty<string>());
    }

    private static JsonObject Parse(string text) => JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
        ?? throw new InvalidDataException("Settings must contain a JSON object.");

    private void Collect(JsonNode? node, string[] path)
    {
        if (path.Length >= 2 && path[0].Equals("OpenAI", StringComparison.OrdinalIgnoreCase) &&
            (path[1].Equals("ApiKey", StringComparison.OrdinalIgnoreCase) || path[1].Equals("EncryptedApiKey", StringComparison.OrdinalIgnoreCase))) return;
        if (node is JsonObject obj)
            foreach (var item in obj) Collect(item.Value, path.Append(item.Key).ToArray());
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++) Collect(array[i], path.Append(i.ToString(CultureInfo.InvariantCulture)).ToArray());
        else _rows.Add(new SettingRow(path, node));
    }

    private static JsonNode? ConvertValue(SettingRow row)
    {
        if (row.Kind == JsonValueKind.String) return JsonValue.Create(row.Value);
        if (row.Kind == JsonValueKind.Null) return row.Value == "null" ? null : JsonValue.Create(row.Value);
        try
        {
            var value = JsonNode.Parse(row.Value);
            var kind = value?.GetValueKind() ?? JsonValueKind.Null;
            if (row.Kind == JsonValueKind.Number && kind == JsonValueKind.Number)
            {
                using var original = JsonDocument.Parse(row.Original);
                using var edited = JsonDocument.Parse(row.Value);
                if (!original.RootElement.TryGetInt64(out _) || edited.RootElement.TryGetInt64(out _)) return value;
                throw new ArgumentException($"{row.Property}: enter a whole number.");
            }
            if (row.Kind is JsonValueKind.True or JsonValueKind.False && kind is JsonValueKind.True or JsonValueKind.False) return value;
        }
        catch (JsonException) { }
        throw new ArgumentException($"{row.Property}: enter {(row.Kind == JsonValueKind.Number ? "a number" : "true or false")}.");
    }

    public void Save()
    {
        if (!HasChanges) return;
        var root = Parse(_snapshot);
        foreach (var row in _rows.Where(row => row.Value != row.Original))
        {
            var value = ConvertValue(row);
            JsonNode parent = root;
            foreach (var segment in row.Path.SkipLast(1))
                parent = parent is JsonArray a ? a[int.Parse(segment, CultureInfo.InvariantCulture)]! : parent[segment]!;
            if (parent is JsonArray array) array[int.Parse(row.Path[^1], CultureInfo.InvariantCulture)] = value;
            else parent[row.Path[^1]] = value;
        }
        if (File.ReadAllText(_path) != _snapshot)
            throw new IOException("The settings file changed since this window opened. Close without saving and reopen Settings to load the latest values.");
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
