using System;

namespace ButchCrawler.App
{
    public interface IMetricsService
    {
        void IncrementError();
        void IncrementEmbeddingFailure();
        void IncrementIndexingFailure();
        void RecordCrawl();

        // Get recent crawl rate (items per minute) over provided window
        double GetCrawlRate(TimeSpan window);

        // Totals
        long TotalErrors { get; }
        long TotalEmbeddingFailures { get; }
        long TotalIndexingFailures { get; }
        long TotalCrawls { get; }
    }
}
