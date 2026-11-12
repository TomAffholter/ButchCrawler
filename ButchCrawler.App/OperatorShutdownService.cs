using Microsoft.Extensions.Hosting;

namespace ButchCrawler.App;

// Only registered for a crawler launched with redirected input by the Operator.
public sealed class OperatorShutdownService(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var command = await Console.In.ReadLineAsync(stoppingToken);
                if (command is null || command.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
