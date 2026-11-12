namespace ButchCrawler.App;

// Transcribing a long recording must not outlive its queue ownership.
public static class AudioQueueLeaseGuard
{
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, Func<Task<bool>> renew,
        CancellationToken ct, TimeSpan? interval = null)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lost = 0;
        async Task RenewAsync()
        {
            using var timer = new PeriodicTimer(interval ?? TimeSpan.FromSeconds(20));
            try
            {
                while (await timer.WaitForNextTickAsync(heartbeat.Token).ConfigureAwait(false))
                {
                    if (await renew().ConfigureAwait(false)) continue;
                    Interlocked.Exchange(ref lost, 1); operation.Cancel(); return;
                }
            }
            catch (OperationCanceledException) when (heartbeat.IsCancellationRequested) { }
            catch { Interlocked.Exchange(ref lost, 1); operation.Cancel(); }
        }
        var keepAlive = RenewAsync();
        try
        {
            var result = await work(operation.Token).ConfigureAwait(false);
            if (Volatile.Read(ref lost) != 0) throw new AudioTranscriptionException("Audio queue ownership was lost. Transcription stopped to avoid duplicate work.", true);
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref lost) != 0)
        { throw new AudioTranscriptionException("Audio queue ownership was lost. Saved segments can resume on the next attempt.", true); }
        finally { heartbeat.Cancel(); await keepAlive.ConfigureAwait(false); }
    }
}
