using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App;

public sealed record HistoricalNewspaperApiResult(string Url, string State, string Title,
    string? PublicationDate, string Snippet);

public sealed class HistoricalNewspaperDiscoveryService
{
    private readonly HttpClient _http;
    private readonly SqlStorage _storage;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HistoricalNewspaperDiscoveryService> _logger;

    public HistoricalNewspaperDiscoveryService(HttpClient http, SqlStorage storage, IConfiguration configuration,
        ILogger<HistoricalNewspaperDiscoveryService> logger)
    { _http = http; _storage = storage; _configuration = configuration; _logger = logger; }

    public async Task DiscoverAndEnqueueAsync(CancellationToken ct)
    {
        if (!_configuration.GetValue<bool>("HistoricalNewspapers:Enabled", true)) return;
        await _storage.EnsureHistoricalNewspaperSchemaAsync(ct).ConfigureAwait(false);
        var selected = (_configuration.GetSection("HistoricalNewspapers:States").Get<string[]>() ?? HistoricalNewspaperCatalog.AllStates.ToArray())
            .Select(HistoricalNewspaperCatalog.NormalizeState).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0) return;
        var keywords = await _storage.GetKeywordsAsync(throwOnError: true).ConfigureAwait(false);
        var maxResults = Math.Clamp(_configuration.GetValue<int>("HistoricalNewspapers:MaxResultsPerKeyword", 40), 1, 160);
        var maxRequests = Math.Clamp(_configuration.GetValue<int>("HistoricalNewspapers:MaxApiRequestsPerRun", 100), 1, 500);
        var delay = Math.Max(3000, _configuration.GetValue<int>("HistoricalNewspapers:RequestDelayMs", 3200));
        var refreshDays = Math.Clamp(_configuration.GetValue<int>("HistoricalNewspapers:RefreshDays", 30), 1, 365);
        var stateText = string.Join(",", HistoricalNewspaperCatalog.AllStates.Where(selected.Contains));
        var requests = 0;
        foreach (var keyword in keywords.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            ct.ThrowIfCancellationRequested();
            if (requests >= maxRequests) break;
            var key = ComputeQueryKey(keyword, selected);
            if (!await _storage.ShouldSearchHistoricalNewspapersAsync(key, refreshDays, ct).ConfigureAwait(false)) continue;
            var found = 0; var queued = 0;
            try
            {
                var query = $"https://www.loc.gov/collections/chronicling-america/?fo=json&at=results,pagination&q={Uri.EscapeDataString('"' + keyword + '"')}&c={maxResults}";
                using var response = await _http.GetAsync(query, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var matches = ParseResults(document.RootElement, keyword, selected);
                found = matches.Count;
                queued = await _storage.SaveHistoricalNewspaperMatchesAsync(matches.Select(x =>
                    new HistoricalNewspaperMatch(x.Url, keyword, x.State, x.Title, x.PublicationDate, x.Snippet)).ToArray(), ct).ConfigureAwait(false);
                await _storage.EnqueueUrlsAsync(matches.Select(x => new EnqueueUrlRequest(x.Url, ParentUrl: query, Depth: 0)).ToArray()).ConfigureAwait(false);
                await _storage.SaveHistoricalNewspaperSearchAsync(key, keyword, stateText, found, queued, "completed", null, ct).ConfigureAwait(false);
                Console.WriteLine($"[CRAWL-NEWSPAPERS] SEARCH | {keyword} | {found} matches | {queued} new");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Historical newspaper search failed for {Keyword}", keyword);
                await _storage.SaveHistoricalNewspaperSearchAsync(key, keyword, stateText, found, queued, "failed", ex.Message, ct).ConfigureAwait(false);
                Console.WriteLine($"[CRAWL-NEWSPAPERS] ERROR | {keyword} | {ex.Message}");
            }
            requests++;
            if (requests < maxRequests) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    public static string ComputeQueryKey(string keyword, IEnumerable<string> states)
    {
        var canonical = keyword.Trim().ToLowerInvariant() + "\n" + string.Join("|", states.Select(HistoricalNewspaperCatalog.NormalizeState)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(x => x.ToLowerInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static IReadOnlyList<HistoricalNewspaperApiResult> ParseResults(JsonElement root, string keyword, ISet<string> selectedStates)
    {
        var output = new List<HistoricalNewspaperApiResult>();
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return output;
        foreach (var item in results.EnumerateArray())
        {
            var state = ReadFirst(item, "location_state");
            if (string.IsNullOrWhiteSpace(state)) state = ReadStateFromLocations(item);
            state = HistoricalNewspaperCatalog.NormalizeState(state ?? string.Empty);
            if (!selectedStates.Contains(state)) continue;
            var url = ReadString(item, "url") ?? ReadString(item, "id");
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) continue;
            var description = ReadJoined(item, "description");
            output.Add(new(url, state, ReadString(item, "title") ?? "Historical newspaper result",
                ReadString(item, "date"), description ?? string.Empty));
        }
        return output.GroupBy(x => x.Url, StringComparer.Ordinal).Select(x => x.First()).ToArray();
    }

    private static string? ReadString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? ReadFirst(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var entry in value.EnumerateArray()) if (entry.ValueKind == JsonValueKind.String) return entry.GetString();
        return null;
    }
    private static string? ReadJoined(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind != JsonValueKind.Array) return null;
        return string.Join("\n", value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
    }
    private static string? ReadStateFromLocations(JsonElement item)
    {
        var joined = ReadJoined(item, "location");
        if (joined == null) return null;
        return HistoricalNewspaperCatalog.AllStates.FirstOrDefault(state => joined.Contains(state, StringComparison.OrdinalIgnoreCase));
    }
}
