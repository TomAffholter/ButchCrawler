using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public class EmbeddingIndexWorker : BackgroundService
    {
        private readonly ILogger<EmbeddingIndexWorker> _logger;
        private readonly IEmbeddingIndexQueue _queue;
        private readonly EmbeddingIndexService _indexService;
        private readonly IMetricsService _metrics;
        private readonly int _concurrency;

        public EmbeddingIndexWorker(ILogger<EmbeddingIndexWorker> logger, IConfiguration config, IEmbeddingIndexQueue queue, EmbeddingIndexService indexService, IMetricsService metrics)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _concurrency = Math.Max(1, config.GetValue<int>("Indexer:MaxConcurrency", 4));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("EmbeddingIndexWorker started with concurrency {Concurrency}", _concurrency);

            var workers = new List<Task>(_concurrency);
            for (int i = 0; i < _concurrency; i++)
            {
                workers.Add(Task.Run(async () =>
                {
                    await foreach (var req in _queue.Reader.ReadAllAsync(stoppingToken))
                    {
                        try
                        {
                            await _indexService.IndexDocumentAsync(req.Url, req.Title, req.Text, req.ContentHash);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Indexing failed for {Url}", req.Url);
                            try { _metrics.IncrementIndexingFailure(); } catch { }
                        }
                    }
                }, stoppingToken));
            }

            // Wait until cancellation then wait for workers to complete
            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }

            _logger.LogInformation("EmbeddingIndexWorker stopping.");
        }
    }
}