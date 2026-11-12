namespace ButchCrawler.App;

public sealed record AudioSegment(int Index, double StartSeconds, string Text);
public sealed record AudioTranscript(string Source, string ContentHash, string Model, string Text, List<AudioSegment> Segments, bool IsComplete = true, int ChunkSeconds = 0);
public sealed record AudioTranscriptSummary(long Id, string Source, string MatchedKeywords, DateTime TranscribedAtUtc, string Preview);

public interface IAudioTranscriptStore
{
    Task<AudioTranscript?> GetAudioTranscriptAsync(string source, string contentHash, string model, CancellationToken ct);
    Task SaveAudioTranscriptAsync(AudioTranscript transcript, CancellationToken ct);
}

public sealed class AudioTranscriptionException(string message, bool retryable = false) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}
