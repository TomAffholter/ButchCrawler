using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace ButchCrawler.App
{
    public class PlaywrightQueue : IPlaywrightQueue
    {
        private readonly PlaywrightService _service;
        private readonly SemaphoreSlim _semaphore;
        private readonly ILogger<PlaywrightQueue>? _logger;
        private readonly RobotsManager _robots;

        public PlaywrightQueue(PlaywrightService service, RobotsManager robots, int capacity, ILogger<PlaywrightQueue>? logger = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _robots = robots ?? throw new ArgumentNullException(nameof(robots));
            _semaphore = new SemaphoreSlim(Math.Max(1, capacity));
            _logger = logger;
        }

        public async Task<RenderResult> EnqueueRenderAsync(string url, int timeout, bool saveScreenshot, bool saveTrace, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));

            // Playwright can occasionally remain inside browser cleanup after its
            // navigation timeout. Bound both the queue wait and the complete render
            // so one damaged page cannot occupy every crawler worker indefinitely.
            var watchdogDelay = TimeSpan.FromMilliseconds(Math.Max(10_000, timeout + 15_000));
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            watchdog.CancelAfter(watchdogDelay);
            try
            {
                await _semaphore.WaitAsync(watchdog.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogWarning("Browser render queue timed out after {Timeout}s for {Url}", watchdogDelay.TotalSeconds, url);
                return new RenderResult { Url = url, FinalUrl = url, Error = "Browser render queue timed out; continuing without JavaScript rendering" };
            }

            try
            {
                // Ensure Playwright is initialized by the worker/host
                var renderTask = _service.RenderPageAsync(url, timeout, saveScreenshot, saveTrace, async (target, token) =>
                {
                    if (!await _robots.IsAllowedAsync(target, token).ConfigureAwait(false)) return false;
                    await _robots.WaitForCrawlDelayAsync(target, token).ConfigureAwait(false);
                    return true;
                }, watchdog.Token);
                try
                {
                    return await renderTask.WaitAsync(watchdog.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Observe any later browser exception without waiting for the
                    // stuck operation. The caller will keep the original HTTP HTML.
                    _ = renderTask.ContinueWith(t => _ = t.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
                    _logger?.LogWarning("Browser render watchdog expired after {Timeout}s for {Url}; HTTP crawl will continue", watchdogDelay.TotalSeconds, url);
                    return new RenderResult { Url = url, FinalUrl = url, Error = "Browser rendering exceeded its time limit; continuing with the downloaded page" };
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
