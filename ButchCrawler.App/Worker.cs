namespace ButchCrawler.App
{
    // Converted to a non-executing helper so there are no top-level statements.
    // This avoids having two entry points (explicit Main + top-level statements).
    public static class HostConfiguration
    {
        public static IHostBuilder ConfigureCrawlerHost(this IHostBuilder builder)
        {
            return builder
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                })
                .ConfigureServices((ctx, services) =>
                {
                    services.AddSingleton<HtmlParser>();
                    services.AddSingleton<SqlStorage>();

                    // Register Fetcher as a typed HttpClient client so DI can supply HttpClient + ILogger<Fetcher>
                    services.AddHttpClient<Fetcher>();

                    services.AddHostedService<CrawlerService>();
                });
        }
    }
}
