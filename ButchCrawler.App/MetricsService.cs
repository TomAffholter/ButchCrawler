using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace ButchCrawler.App
{
    public class MetricsService : IMetricsService
    {
        private long _errors;
        private long _embeddingFailures;
        private long _indexingFailures;
        private long _totalCrawls;

        // timestamps of crawls for rate calculation
        private readonly ConcurrentQueue<DateTime> _crawlTimestamps = new();

        public long TotalErrors => Interlocked.Read(ref _errors);
        public long TotalEmbeddingFailures => Interlocked.Read(ref _embeddingFailures);
        public long TotalIndexingFailures => Interlocked.Read(ref _indexingFailures);
        public long TotalCrawls => Interlocked.Read(ref _totalCrawls);

        public void IncrementError()
        {
            Interlocked.Increment(ref _errors);
        }

        public void IncrementEmbeddingFailure()
        {
            Interlocked.Increment(ref _embeddingFailures);
        }

        public void IncrementIndexingFailure()
        {
            Interlocked.Increment(ref _indexingFailures);
        }

        public void RecordCrawl()
        {
            Interlocked.Increment(ref _totalCrawls);
            _crawlTimestamps.Enqueue(DateTime.UtcNow);
            // optional: prune old entries lazily
            PruneOldTimestamps(TimeSpan.FromMinutes(10));
        }

        public double GetCrawlRate(TimeSpan window)
        {
            PruneOldTimestamps(window);
            var cutoff = DateTime.UtcNow - window;
            var count = _crawlTimestamps.Count(ts => ts > cutoff);
            if (window.TotalMinutes <= 0) return 0.0;
            return count / window.TotalMinutes;
        }

        private void PruneOldTimestamps(TimeSpan window)
        {
            var cutoff = DateTime.UtcNow - window;
            while (_crawlTimestamps.TryPeek(out var ts) && ts < cutoff)
            {
                _crawlTimestamps.TryDequeue(out _);
            }
        }
    }
}
