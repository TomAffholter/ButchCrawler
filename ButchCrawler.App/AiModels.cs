namespace ButchCrawler.App;

public sealed record AiSource(string Id, string Source, string Text);
public sealed record AiDocument(string Source, string Kind, int Characters, string Preview);
public sealed record AiCitation(string SourceId, string Quote, string Source = "");
public sealed record AiFinding(string Kind, string Name, string SourceId, string Quote, string Source = "");
public sealed record AiResult(string Answer, List<AiCitation> Citations, List<AiFinding> Findings, List<string> Uncertainties, bool NeedsReview, bool InsufficientEvidence);
public sealed record AiHandwriting(string Transcription, List<string> Uncertainties, bool NeedsReview);
public sealed record AiSavedResult(long Id, string Task, string Source, string InputHash, string Model, string OutputText, string Json, bool Reviewed, string? ReviewedText, DateTime CreatedAtUtc = default);

public interface IAiRepository
{
    Task<AiSavedResult?> GetAiResultAsync(string task, string source, string hash, string model, CancellationToken ct);
    Task<AiSavedResult> SaveAiResultAsync(string task, string source, string hash, string model, string text, string json, CancellationToken ct);
    Task<bool> ConsumeAiRequestAsync(int dailyLimit, CancellationToken ct);
    Task<IAsyncDisposable> AcquireAiWorkAsync(string key, CancellationToken ct);
}

public sealed class AiResearchException(string message, bool retryable = false) : Exception(message)
{ public bool Retryable { get; } = retryable; }
