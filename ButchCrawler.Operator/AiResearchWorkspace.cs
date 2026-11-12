namespace ButchCrawler.Operator;

public enum AiResearchFeature { Question, Summary, Entities, Handwriting }
public sealed record AiResearchDocument(string Id, string Source, string Preview)
{
    public string TypeLabel => Id switch { "crawl" => "Web page", "document" => "Document / OCR", "audio" => "Audio transcript", _ => Id };
    public string SearchLabel => $"{TypeLabel} · {Source}\n{Preview}";
}
public sealed record AiResearchSource(string Label, string Source, string Passage);
public sealed record AiResearchResult(string Id, string Feature, string Text, string Model,
    string Usage, DateTime CreatedAt, IReadOnlyList<AiResearchSource> Sources)
{
    public string HistoryLabel => $"{CreatedAt.ToLocalTime():g} · {Feature} · #{Id}";
}
public sealed record AiResearchRequest(AiResearchFeature Feature, string Question, AiResearchDocument? Document, string? ImagePath);

public interface IAiResearchWorkspace : IDisposable
{
    string LimitsDescription { get; }
    Task<IReadOnlyList<AiResearchDocument>> SearchAsync(string query, CancellationToken ct);
    Task<string> ReadDocumentAsync(AiResearchDocument document, CancellationToken ct);
    Task<AiResearchResult> RunAsync(AiResearchRequest request, CancellationToken ct);
    Task<IReadOnlyList<AiResearchResult>> HistoryAsync(CancellationToken ct);
}
