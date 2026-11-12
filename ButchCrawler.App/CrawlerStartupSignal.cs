namespace ButchCrawler.App;

public sealed record CrawlerStartupResult(bool Success, string? Error);

// Separates "the .NET host started" from "the crawler passed its database and
// queue startup checks." The Operator must not claim the crawler is running
// until the latter is true.
public sealed class CrawlerStartupSignal
{
    private readonly TaskCompletionSource<CrawlerStartupResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CrawlerStartupResult> Completion => _completion.Task;
    public void Ready() => _completion.TrySetResult(new CrawlerStartupResult(true, null));
    public void Fail(string error) => _completion.TrySetResult(new CrawlerStartupResult(false, error));
}
