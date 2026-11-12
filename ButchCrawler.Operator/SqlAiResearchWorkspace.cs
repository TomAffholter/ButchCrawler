using System.IO;
using System.Net.Http;
using ButchCrawler.App;
using Microsoft.Extensions.Configuration;

namespace ButchCrawler.Operator;

public sealed class SqlAiResearchWorkspace : IAiResearchWorkspace
{
    private readonly IConfigurationRoot _config;
    private readonly HttpClient _http;
    private readonly SqlAiRepository _repository;
    private readonly AiResearchService _service;
    private readonly string _settingsPath;
    private bool _initialized;
    public SqlAiResearchWorkspace(string settingsPath, HttpClient? http = null)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        _config = new ConfigurationBuilder().SetBasePath(directory).AddJsonFile(Path.GetFileName(settingsPath))
            .AddJsonFile($"appsettings.{environment}.json", optional: true).AddEnvironmentVariables().Build();
        try
        {
            _repository = new SqlAiRepository(_config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Missing database connection."));
            _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _service = new AiResearchService(new AiJsonClient(_http, _config, _repository), _config, _repository);
        }
        catch { (_config as IDisposable)?.Dispose(); throw; }
    }
    public string LimitsDescription => _config.GetValue<bool>("AI:Enabled", false)
        ? $"AI is enabled, with up to {Math.Clamp(_config.GetValue<int>("AI:DailyRequestLimit", 50), 1, 10000)} API requests per UTC day, including retries and document sections. Cached results use no new requests."
        : "AI is currently off. Free collection search and previously saved results remain available. Add a key or change optional features from Settings when you want to use paid AI tools.";
    private async Task InitializeAsync(CancellationToken ct)
    { if (!_initialized) { await _repository.InitializeAsync(ct); _initialized = true; } }
    public async Task<IReadOnlyList<AiResearchDocument>> SearchAsync(string query, CancellationToken ct)
    {
        var docs = await _repository.SearchAsync(query, ct);
        return docs.Select(d => new AiResearchDocument(d.Kind, d.Source, d.Preview)).ToArray();
    }
    public Task<string> ReadDocumentAsync(AiResearchDocument document, CancellationToken ct) => _repository.ReadDocumentAsync(document.Source, document.Id, ct);
    public async Task<AiResearchResult> RunAsync(AiResearchRequest request, CancellationToken ct)
    {
        // Ordinary collection search and saved-result viewing never need to decrypt a key.
        // Resolve it only when the user deliberately starts a paid AI operation.
        if (string.IsNullOrWhiteSpace(_config["OpenAI:ApiKey"]))
            _config["OpenAI:ApiKey"] = new OpenAiKeyStore(_settingsPath).ReadKey();
        await InitializeAsync(ct);
        AiSavedResult saved;
        if (request.Feature == AiResearchFeature.Handwriting)
        {
            var path = request.ImagePath ?? throw new AiResearchException("Choose an image first.");
            var limit = _config.GetValue<int>("AI:MaxImageBytes", 10_000_000);
            if (new FileInfo(path).Length > limit) throw new AiResearchException($"Choose an image smaller than {limit:N0} bytes (AI : MaxImageBytes in Settings).");
            var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", _ => throw new AiResearchException("Choose PNG, JPEG or WebP.") };
            saved = await _service.TranscribeImageAsync(path, await File.ReadAllBytesAsync(path, ct), mime, ct);
        }
        else if (request.Feature == AiResearchFeature.Question)
        {
            var docs = await _repository.SearchAsync(request.Question, ct);
            var sources = new List<AiSource>();
            var terms = SqlAiRepository.SearchTerms(request.Question);
            if (terms.Length == 0) throw new AiResearchException("Include a specific name, place, date or topic in your question.");
            foreach (var doc in docs.DistinctBy(d => d.Source).Take(12))
            {
                var text = await _repository.ReadDocumentAsync(doc.Source, doc.Kind, ct);
                // Keep passages around matched terms instead of silently using only document beginnings.
                var positions = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(p => p >= 0).Distinct().Order().ToArray();
                var passages = new List<string>(); var end = -1;
                foreach (var position in positions)
                {
                    if (position < end || passages.Count == 3) continue;
                    var start = Math.Max(0, position - 500); end = Math.Min(text.Length, start + 2500);
                    passages.Add(text[start..end]);
                }
                if (passages.Count == 0) passages.Add(text[..Math.Min(2500, text.Length)]);
                foreach (var passage in passages)
                {
                    if (sources.Count == 12) break;
                    sources.Add(new AiSource("S" + (sources.Count + 1), doc.Source, passage));
                }
                if (sources.Count == 12) break;
            }
            saved = await _service.AnswerAsync(request.Question, sources, ct);
        }
        else
        {
            var document = request.Document ?? throw new AiResearchException("Select a saved source first.");
            saved = await _service.AnalyzeAsync(document.Source, await ReadDocumentAsync(document, ct), ct);
        }
        return Present(saved, request.Feature);
    }
    public async Task<IReadOnlyList<AiResearchResult>> HistoryAsync(CancellationToken ct)
    { await InitializeAsync(ct); return (await _repository.HistoryAsync(ct)).Select(s => Present(s)).ToArray(); }
    private static AiResearchResult Present(AiSavedResult saved, AiResearchFeature? feature = null)
    {
        string text, label; IReadOnlyList<AiResearchSource> sources;
        if (saved.Task == AiResearchService.HandwritingTask)
        {
            var result = AiResearchService.ParseHandwriting(saved); label = "Handwriting";
            text = result.Transcription + Uncertainties(result.Uncertainties);
            sources = new[] { new AiResearchSource("Original image", saved.Source, "Compare this transcription with the original image.") };
        }
        else
        {
            var result = AiResearchService.ParseResult(saved);
            label = saved.Task == AiResearchService.AnswerTask ? "Collection answer" : "Summary and people / places / dates";
            var findings = string.Join("\n\n", result.Findings.Select(f => $"{f.Kind}: {f.Name}\n[{f.SourceId}] {f.Quote}"));
            text = feature == AiResearchFeature.Entities ? findings : result.Answer;
            if (string.IsNullOrWhiteSpace(text)) text = "No supported findings were returned.";
            if (feature != AiResearchFeature.Entities && findings.Length > 0) text += "\n\nPeople, places, dates and connections\n" + findings;
            text += Uncertainties(result.Uncertainties);
            if (result.InsufficientEvidence) text += "\n\nThe supplied evidence is insufficient for a complete answer.";
            sources = result.Citations.Select(c => new AiResearchSource(c.SourceId, c.Source, c.Quote))
                .Concat(result.Findings.Select(f => new AiResearchSource(f.SourceId, f.Source, f.Quote))).Distinct().ToArray();
        }
        text += "\n\nAI-generated. Verify against the original sources.";
        return new(saved.Id.ToString(), label, text, saved.Model, "Saved separately from the original source.", saved.CreatedAtUtc, sources);
    }
    private static string Uncertainties(List<string> items) => items.Count == 0 ? "" : "\n\nUncertain readings / research gaps\n" + string.Join("\n", items.Select(s => "• " + s));
    public void Dispose() { _http.Dispose(); (_config as IDisposable)?.Dispose(); }
}
