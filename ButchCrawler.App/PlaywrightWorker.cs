using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public class PlaywrightWorker : IHostedService
    {
        private readonly PlaywrightService _svc;
        private readonly ILogger<PlaywrightWorker> _logger;

        public PlaywrightWorker(PlaywrightService svc, ILogger<PlaywrightWorker> logger)
        {
            _svc = svc ?? throw new ArgumentNullException(nameof(svc));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _svc.InitializeAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Browser rendering is unavailable for this run: {Reason}", ex.GetBaseException().Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _svc.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Playwright disposal failed");
            }
        }
    }
}
