namespace ButchCrawler.App;

// Keep ownership while waiting on robots, host spacing, downloads and extraction.
public sealed class QueueLeaseHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource _work;
    private readonly CancellationTokenSource _heartbeat = new();
    private readonly Task _loop;
    public CancellationToken Token => _work.Token;
    public bool OwnershipLost { get; private set; }

    public QueueLeaseHeartbeat(Func<Task<bool>> renew, CancellationToken ct, TimeSpan? interval = null)
    {
        _work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = RunAsync(renew, interval ?? TimeSpan.FromSeconds(20));
    }

    private async Task RunAsync(Func<Task<bool>> renew, TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_heartbeat.Token).ConfigureAwait(false))
            {
                if (await renew().ConfigureAwait(false)) continue;
                OwnershipLost = true;
                _work.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (_heartbeat.IsCancellationRequested) { }
        catch { OwnershipLost = true; _work.Cancel(); }
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeat.Cancel();
        await _loop.ConfigureAwait(false);
        _heartbeat.Dispose();
        _work.Dispose();
    }
}
