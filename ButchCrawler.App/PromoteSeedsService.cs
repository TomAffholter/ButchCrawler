using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    // Minimal hosted service placeholder for optional seed promotion functionality
    public class PromoteSeedsService : BackgroundService
    {
        private readonly ILogger<PromoteSeedsService> _logger;

        public PromoteSeedsService(ILogger<PromoteSeedsService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("PromoteSeedsService started (no-op)");
            try
            {
                // Keep running but idle; implement promotion logic later if desired
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
            catch (OperationCanceledException) { }
            _logger.LogInformation("PromoteSeedsService stopped");
        }
    }
}
