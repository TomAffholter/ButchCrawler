using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    public sealed class HostConcurrencyLimiter
    {
        private readonly int _maxPerHost;
        private readonly TimeSpan _minDelay;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _lastStart = new(StringComparer.OrdinalIgnoreCase);

        public HostConcurrencyLimiter(int maxPerHost = 4, TimeSpan? minDelay = null)
        {
            _maxPerHost = Math.Max(1, maxPerHost);
            _minDelay = minDelay ?? TimeSpan.FromMilliseconds(500);
        }

        // Returns an IDisposable that releases the host slot when disposed.
        public async Task<IDisposable> WaitAsync(string host, CancellationToken ct)
        {
            var sem = _semaphores.GetOrAdd(host, _ => new SemaphoreSlim(_maxPerHost, _maxPerHost));
            await sem.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var hostLock = _locks.GetOrAdd(host, _ => new object());
                // Recheck after every delay: delayed continuations must not start together.
                while (true)
                {
                    TimeSpan wait;
                    lock (hostLock)
                    {
                        var now = DateTime.UtcNow;
                        wait = _minDelay - (now - _lastStart.GetValueOrDefault(host, DateTime.MinValue));
                        if (wait <= TimeSpan.Zero)
                        {
                            _lastStart[host] = now;
                            return new Releaser(host, this);
                        }
                    }
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                sem.Release();
                throw;
            }
        }

        private void Release(string host)
        {
            if (_semaphores.TryGetValue(host, out var s)) s.Release();
        }

        private sealed class Releaser : IDisposable
        {
            private readonly string _host;
            private readonly HostConcurrencyLimiter _owner;
            private bool _disposed;

            public Releaser(string host, HostConcurrencyLimiter owner) { _host = host; _owner = owner; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.Release(_host);
            }
        }
    }
}
