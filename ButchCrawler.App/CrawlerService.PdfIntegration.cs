using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    // Per-host concurrency + crawl-delay limiter with jitter. Safe for multi-threaded use.
    public sealed class HostRateLimiter
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
        private readonly ConcurrentDictionary<string, DateTime> _nextRequestUtc = new();
        private readonly int _perHostConcurrency;
        private readonly TimeSpan _defaultDelay;
        private readonly Random _rand = new();

        public HostRateLimiter(int perHostConcurrency = 1, TimeSpan? defaultDelay = null)
        {
            _perHostConcurrency = Math.Max(1, perHostConcurrency);
            _defaultDelay = defaultDelay ?? TimeSpan.FromSeconds(1);
        }

        private static string GetHost(string url)
        {
            try { return new Uri(url).Host.ToLowerInvariant(); }
            catch { return string.Empty; }
        }

        // AcquireAsync returns an IDisposable that releases the semaphore when disposed.
        public async Task<IDisposable> AcquireAsync(string url, CancellationToken ct)
        {
            if (url is null) throw new ArgumentNullException(nameof(url));
            var host = GetHost(url);
            var sem = _semaphores.GetOrAdd(host, _ => new SemaphoreSlim(_perHostConcurrency, _perHostConcurrency));

            await sem.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        sem.Release();
                        ct.ThrowIfCancellationRequested();
                    }

                    var next = _nextRequestUtc.GetOrAdd(host, DateTime.UtcNow);
                    var now = DateTime.UtcNow;
                    if (next <= now)
                    {
                        // schedule next request time with jitter +/-25%
                        var jitterMs = (int)(_defaultDelay.TotalMilliseconds * (0.75 + _rand.NextDouble() * 0.5));
                        _nextRequestUtc[host] = DateTime.UtcNow + TimeSpan.FromMilliseconds(jitterMs);
                        return new Releaser(sem);
                    }

                    var wait = next - now;
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // ensure semaphore released on error
                try { sem.Release(); } catch { /* ignore */ }
                throw;
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly SemaphoreSlim _sem;
            private bool _disposed;
            public Releaser(SemaphoreSlim sem) => _sem = sem;
            public void Dispose()
            {
                if (!_disposed)
                {
                    _sem.Release();
                    _disposed = true;
                }
            }
        }
    }
}
