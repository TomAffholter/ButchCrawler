using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButchCrawler.App;

namespace ButchCrawler.Operator;

public sealed record HistoricalNewspaperSettings(bool Enabled, IReadOnlyList<string> States,
    int MaxResultsPerKeyword, int RefreshDays);

public sealed class HistoricalNewspaperSettingsRepository(string path)
{
    public HistoricalNewspaperSettings Load()
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("appsettings.json is empty or invalid.");
        var section = root["HistoricalNewspapers"] as JsonObject;
        var enabled = section?["Enabled"]?.GetValue<bool>() ?? true;
        var states = (section?["States"] as JsonArray)?.Select(x => x?.GetValue<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => HistoricalNewspaperCatalog.NormalizeState(x!))
            .Where(x => HistoricalNewspaperCatalog.AllStates.Contains(x, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? HistoricalNewspaperCatalog.AllStates.ToArray();
        return new(enabled, states,
            Math.Clamp(section?["MaxResultsPerKeyword"]?.GetValue<int>() ?? 40, 1, 160),
            Math.Clamp(section?["RefreshDays"]?.GetValue<int>() ?? 30, 1, 365));
    }

    public void Save(HistoricalNewspaperSettings settings)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException("appsettings.json is empty or invalid.");
        var section = root["HistoricalNewspapers"] as JsonObject ?? new JsonObject();
        root["HistoricalNewspapers"] = section;
        section["Enabled"] = settings.Enabled;
        section["Provider"] = "LibraryOfCongress";
        section["States"] = new JsonArray(settings.States.Select(state => (JsonNode?)JsonValue.Create(state)).ToArray());
        section["MaxResultsPerKeyword"] = Math.Clamp(settings.MaxResultsPerKeyword, 1, 160);
        section["MaxApiRequestsPerRun"] = 100;
        section["RequestDelayMs"] = 3200;
        section["RefreshDays"] = Math.Clamp(settings.RefreshDays, 1, 365);
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, json); File.Move(temporary, path, overwrite: true); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
}
