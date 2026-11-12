using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using ButchCrawler.App;
    
var loggingControl = new LoggingControl();

// Configure Serilog early so we capture startup logs and unhandled exceptions.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs\\butchcrawler-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("ButchCrawler starting up. Args: {Args}", Environment.GetCommandLineArgs());

    var host = Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((context, configuration) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            var current = configuration.Build();
            try
            {
                if (!current.GetValue<bool>("AI:Enabled", false) && !current.GetValue<bool>("Audio:Enabled", false) &&
                    !current.GetValue<bool>("Indexer:Enabled", false)) return;
            }
            finally { (current as IDisposable)?.Dispose(); }
            try
            {
                var key = new OpenAiKeyStore(System.IO.Path.Combine(context.HostingEnvironment.ContentRootPath, "appsettings.json")).ReadKey();
                if (!string.IsNullOrWhiteSpace(key))
                    configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = key });
            }
            catch
            {
                throw new InvalidOperationException("Cannot decrypt the saved OpenAI key. Open the Operator under the Windows user that saved it.");
            }
        })
        .UseSerilog() // route Microsoft.Extensions.Logging through Serilog
        .ConfigureServices((context, services) =>
        {
            var config = context.Configuration;
            services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
            if (config.GetValue<bool>("Operator:Controlled", false)) services.AddHostedService<OperatorShutdownService>();

            // register the logging control so services can toggle behavior at runtime
            services.AddSingleton(loggingControl);
            services.AddSingleton<CrawlerStartupSignal>();

            // ICaptchaSolver registration
            if (config.GetValue<bool>("Crawler:EnableNopeCha", false))
                services.AddHttpClient<ICaptchaSolver, NopeChaCaptchaSolver>();
            else
                services.AddSingleton<ICaptchaSolver, NoopCaptchaSolver>();

            // HTTP typed client for Fetcher
            services.AddHttpClient<Fetcher>()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    MaxConnectionsPerServer = config.GetValue<int>("Crawler:MaxConnectionsPerServer", 20),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                });

            services.AddHttpClient<EmbeddingService>();
            services.AddHttpClient<HistoricalNewspaperDiscoveryService>(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(config.GetValue<string>("Crawler:UserAgent", "ButchCrawler/1.0"));
            });
            services.AddSingleton<IAudioTranscriptStore>(sp => sp.GetRequiredService<SqlStorage>());
            services.AddHttpClient<AudioProcessor>(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            services.AddSingleton<EmbeddingIndexService>();
            services.AddSingleton<HtmlParser>();
            services.AddSingleton<IMetricsService, MetricsService>();

            services.AddSingleton<SqlStorage>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var conn = cfg.GetConnectionString("DefaultConnection")
                           ?? cfg.GetValue<string>("ConnectionStrings:DefaultConnection")
                           ?? throw new InvalidOperationException("Missing DB connection string");
                var logger = sp.GetRequiredService<ILogger<SqlStorage>>();
                return new SqlStorage(conn, logger);
            });

            services.AddSingleton<RobotsManager>(sp => new RobotsManager(null,
                sp.GetRequiredService<ILogger<RobotsManager>>(), config));

            var perHostConcurrency = config.GetValue<int>("Crawler:PerHostConcurrency", 1);
            var defaultDelaySeconds = config.GetValue<int>("Crawler:DefaultCrawlDelaySeconds", 1);
            services.AddSingleton(new HostRateLimiter(perHostConcurrency, TimeSpan.FromSeconds(defaultDelaySeconds)));

            services.AddSingleton<IPdfProcessor, TesseractPdfProcessor>();
            services.AddSingleton<StructuredDataProcessor>();

            services.AddSingleton<PayloadProcessorRegistry>(sp =>
            {
                var reg = new PayloadProcessorRegistry();
                var docx = new DocxProcessor();
                reg.RegisterMime("application/vnd.openxmlformats-officedocument.wordprocessingml.document", docx);
                reg.RegisterExtension(".docx", docx);
                var mht = new MhtProcessor();
                reg.RegisterExtension(".mht", mht);
                reg.RegisterExtension(".mhtml", mht);
                var imageProcessor = ActivatorUtilities.CreateInstance<ImageProcessor>(sp);
                foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp" })
                    reg.RegisterExtension(extension, imageProcessor);
                reg.RegisterMime("image/jpeg", imageProcessor);
                reg.RegisterMime("image/png", imageProcessor);
                reg.RegisterMime("image/tiff", imageProcessor);
                reg.RegisterMime("image/webp", imageProcessor);
                var structured = sp.GetRequiredService<StructuredDataProcessor>();
                foreach (var extension in StructuredDataDetector.SupportedExtensions)
                    reg.RegisterExtension(extension, structured);
                foreach (var mime in new[]
                {
                    "text/csv", "text/tab-separated-values", "application/json", "text/json",
                    "application/ld+json", "application/geo+json", "application/xml", "text/xml",
                    "application/sql", "text/sql", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "application/vnd.ms-excel", "application/vnd.sqlite3", "application/x-sqlite3",
                    "application/x-msaccess", "application/x-dbf", "application/zip", "application/x-zip-compressed"
                }) reg.RegisterMime(mime, structured);
                sp.GetRequiredService<AudioProcessor>().Register(reg);
                return reg;
            });

            services.AddSingleton<IEmbeddingIndexQueue>(_ => new EmbeddingIndexQueue(capacity: 2000));
            if (config.GetValue<bool>("Indexer:Enabled", false)) services.AddHostedService<EmbeddingIndexWorker>();

            services.AddSingleton<PlaywrightService>();
            services.AddSingleton<IPlaywrightQueue>(sp => ActivatorUtilities.CreateInstance<PlaywrightQueue>(sp, Math.Max(1, config.GetValue<int>("Playwright:MaxConcurrency", 1))));
            if (config.GetValue<bool>("Playwright:Enabled", true)) services.AddHostedService<PlaywrightWorker>();

            // Register semantic search service (in-process)
            services.AddSingleton<EmbeddingSearchService>();

            services.AddHostedService<CrawlerService>();
            services.AddHostedService<HeartbeatService>();
            if (config.GetValue<bool>("LocalCrawler:Enabled", false)) services.AddHostedService<LocalFileCrawler>();
        })
        .ConfigureLogging((ctx, logging) =>
        {
            // keep console provider (Serilog writes to console/file)
            logging.AddConsole();

            // dynamic filter consulting loggingControl (still honored)
            logging.AddFilter((category, level) =>
            {
                if (level >= LogLevel.Warning) return true;
                if (level == LogLevel.Information && loggingControl.SuppressInfo) return false;
                return level >= LogLevel.Information;
            });

            // allow Debug/Information in pipeline; Serilog controls sinks
            logging.SetMinimumLevel(LogLevel.Debug);
        })
        .UseConsoleLifetime()
        .Build();

    // Global handlers to ensure unhandled errors are recorded by Serilog
    AppDomain.CurrentDomain.UnhandledException += (s, e) =>
    {
        try
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (AppDomain)");
            Log.CloseAndFlush();
        }
        catch { }
    };

    TaskScheduler.UnobservedTaskException += (s, e) =>
    {
        try
        {
            Log.Fatal(e.Exception, "Unobserved task exception");
            e.SetObserved();
        }
        catch { }
    };

    await host.StartAsync();
    var startupSignal = host.Services.GetRequiredService<CrawlerStartupSignal>();
    var applicationLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    CrawlerStartupResult startupResult;
    try
    {
        startupResult = await startupSignal.Completion.WaitAsync(
            TimeSpan.FromMinutes(3), applicationLifetime.ApplicationStopping);
    }
    catch (OperationCanceledException) when (applicationLifetime.ApplicationStopping.IsCancellationRequested)
    {
        await host.StopAsync();
        return;
    }
    catch (TimeoutException)
    {
        startupResult = new CrawlerStartupResult(false, "Crawler startup checks did not finish within three minutes.");
    }

    if (!startupResult.Success)
    {
        Console.WriteLine($"[CRAWL-STATE] FAILED | {startupResult.Error}");
        Environment.ExitCode = 1;
        await host.StopAsync();
        return;
    }

    Console.WriteLine("[CRAWL-STATE] RUNNING");
    await host.WaitForShutdownAsync();
}
finally
{
    // Human-verification cookies are needed only by the crawler run that the
    // user explicitly started. Do not retain them after that process exits.
    try { HumanVerificationSessionStore.DeleteAll(); } catch { }
    try { Log.CloseAndFlush(); } catch { }
}
