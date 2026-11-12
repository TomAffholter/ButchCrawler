using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace ButchCrawler.App
{
    /// <summary>
    /// Periodically logs heartbeat messages with light-weight metrics.
    /// </summary>
    public class HeartbeatService : BackgroundService
    {
        private readonly ILogger<HeartbeatService> _logger;
        private readonly IConfiguration _config;
        private readonly SqlStorage _storage;
        private readonly TimeSpan _interval;

        public HeartbeatService(ILogger<HeartbeatService> logger, IConfiguration config, SqlStorage storage)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));

            var seconds = Math.Max(5, _config.GetValue<int>("Heartbeat:IntervalSeconds", 60));
            _interval = TimeSpan.FromSeconds(seconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HeartbeatService starting (interval={Interval}s)", _interval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var stats = await _storage.GetCrawlQueueStatsAsync();
                    var visited = await _storage.GetVisitedCountAsync();
                    var seedProgress = await _storage.GetActiveSeedProgressAsync(stoppingToken);

                    _logger.LogInformation("Heartbeat: Pending={Pending}, Locked={Locked}, Completed={Completed}, Visited={Visited}, Time={Time}",
                        stats.Pending, stats.Locked, stats.Completed, visited, DateTime.UtcNow.ToString("o"));
                    Console.WriteLine($"[CRAWL-SEEDS] PROGRESS | {seedProgress.Successful} | {seedProgress.Problems} | {seedProgress.Total}");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Heartbeat tick failed");
                }

                try
                {
                    await Task.Delay(_interval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            _logger.LogInformation("HeartbeatService stopping.");
        }
    }
}
