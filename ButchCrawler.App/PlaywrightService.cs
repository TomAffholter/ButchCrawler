using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace ButchCrawler.App
{
    /// <summary>
    /// Lightweight Playwright wrapper for rendering JS-heavy pages.
    /// Singleton service: creates a browser instance and provides RenderPageAsync.
    /// </summary>
    public class PlaywrightService : IAsyncDisposable
    {
        private readonly ILogger<PlaywrightService> _logger;
        private readonly IConfiguration _config;
        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private bool _initialized;
        private bool _initializationAttempted;
        private string? _initializationError;

        public PlaywrightService(ILogger<PlaywrightService> logger, IConfiguration config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            if (_initialized) return;
            if (_initializationAttempted)
                throw new InvalidOperationException(_initializationError ?? "Browser rendering is unavailable for this run.");

            _initializationAttempted = true;

            try
            {

            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            var headless = _config.GetValue<bool>("Playwright:Headless", true);

            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = headless
                });
            }
            catch (PlaywrightException) when (_config.GetValue<bool>("Playwright:UseInstalledEdgeFallback", true))
            {
                // The Playwright NuGet package does not itself contain a browser binary.
                // A normal Windows installation already has Microsoft Edge, so use it
                // rather than disabling JavaScript recovery or requiring a second browser.
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = headless,
                    Channel = "msedge"
                });
                _logger.LogInformation("Playwright is using the installed Microsoft Edge browser");
            }

            _initialized = true;
            _logger.LogInformation("Playwright initialized (headless={Headless})", headless);
            }
            catch (Exception ex)
            {
                _initializationError = ex.GetBaseException().Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    ?? "Browser rendering could not be initialized.";
                throw;
            }
        }

        /// <summary>
        /// Renders a page, captures HTML, a screenshot, and a trace zip.
        /// </summary>
        public async Task<RenderResult> RenderPageAsync(string url, int timeoutMs = 30_000, bool saveScreenshot = false,
            bool saveTrace = false, Func<string, CancellationToken, Task<bool>>? allowNavigation = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentNullException(nameof(url));
            if (!_initialized) await InitializeAsync(ct);

            var result = new RenderResult { Url = url, FinalUrl = url, StatusCode = 0 };
            if (!_config.GetValue<bool>("Crawler:AllowPrivateNetwork", false) &&
                !await NetworkAccessPolicy.IsPublicUriAsync(url, ct).ConfigureAwait(false))
                return new RenderResult { Url = url, FinalUrl = url, Error = "Destination is not a public HTTP(S) address" };

            // Small lifetime scope for context/page to avoid sharing cross-requests.
            // If the user recently completed human verification for this host,
            // seed this isolated context with that short-lived browser state.
            var contextOptions = new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
                IgnoreHTTPSErrors = false,
                UserAgent = _config.GetValue<string>("Crawler:UserAgent") ?? "ButchCrawler/1.0"
            };
            var sessionHours = Math.Clamp(_config.GetValue<int>("Playwright:HumanVerificationSessionHours", HumanVerificationSessionStore.DefaultLifetimeHours), 1, 24);
            var storageStatePath = HumanVerificationSessionStore.CreateReadableStorageStateFile(url, sessionHours);
            if (storageStatePath != null)
            {
                contextOptions.StorageStatePath = storageStatePath;
                // Verification services commonly bind the clearance cookie to the
                // browser fingerprint. Match the normal visible Edge/Chromium UA.
                contextOptions.UserAgent = null;
            }
            IBrowserContext context;
            try { context = await _browser!.NewContextAsync(contextOptions); }
            catch (PlaywrightException) when (storageStatePath != null)
            {
                // The encrypted state could no longer be loaded by the browser.
                // Remove both short-lived and remembered copies so the UI asks
                // for a fresh sign-in instead of failing on every crawl run.
                HumanVerificationSessionStore.Delete(url);
                contextOptions.StorageStatePath = null;
                context = await _browser!.NewContextAsync(contextOptions);
            }
            finally { if (storageStatePath != null) try { File.Delete(storageStatePath); } catch { } }
            await using var contextScope = context;

            // Start tracing to capture network and other events - trace is a ZIP
            if (saveTrace) await contextScope.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = saveScreenshot,
                Snapshots = true,
                Sources = false
            });

            var page = await contextScope.NewPageAsync();
            page.SetDefaultTimeout(timeoutMs);
            page.SetDefaultNavigationTimeout(timeoutMs);
            await page.RouteAsync("**/*", async route =>
            {
                var requestUrl = route.Request.Url;
                if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var requestUri))
                {
                    await route.AbortAsync("blockedbyclient");
                    return;
                }
                var isHttp = requestUri.Scheme == Uri.UriSchemeHttp || requestUri.Scheme == Uri.UriSchemeHttps;
                var isPageLocal = requestUri.Scheme is "data" or "blob" or "about";
                if (!isHttp && !isPageLocal)
                {
                    await route.AbortAsync("blockedbyclient");
                    return;
                }
                if (isHttp && !_config.GetValue<bool>("Crawler:AllowPrivateNetwork", false) &&
                    !await NetworkAccessPolicy.IsPublicUriAsync(requestUrl, ct).ConfigureAwait(false))
                {
                    await route.AbortAsync("blockedbyclient");
                    return;
                }
                if (isHttp && route.Request.ResourceType == "document" && allowNavigation != null &&
                    !await allowNavigation(requestUrl, ct).ConfigureAwait(false))
                {
                    await route.AbortAsync("blockedbyclient");
                    return;
                }
                await route.ContinueAsync();
            });

            try
            {
                var nav = await page.GotoAsync(url, new PageGotoOptions
                {
                    Timeout = timeoutMs,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });

                // Final URL after redirects
                result.FinalUrl = page.Url;
                result.StatusCode = nav?.Status ?? 200;

                var settleMs = Math.Clamp(_config.GetValue<int>("Playwright:ContentSettleMs", 750), 0, 5000);
                if (settleMs > 0) await page.WaitForTimeoutAsync(settleMs);

                // Expose HTML and screenshot
                result.Html = await page.ContentAsync();
                if (saveScreenshot)
                    result.Screenshot = await page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true });

                // Stop tracing into a MemoryStream
                if (saveTrace)
                {
                    var tmpPath = Path.GetTempFileName();
                    try
                    {
                        await contextScope.Tracing.StopAsync(new TracingStopOptions { Path = tmpPath });
                        result.TraceZip = await File.ReadAllBytesAsync(tmpPath, ct);
                    }
                    finally { try { File.Delete(tmpPath); } catch { } }
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _logger.LogWarning(ex, "Playwright render failed for {Url}", url);
                result.Error = ex.Message;
            }
            finally
            {
                // Browser shutdown is best-effort. Never let a broken renderer
                // hold the crawler queue forever during page/context cleanup.
                try { await page.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                try { await contextScope.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }

            return result;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_browser != null) await _browser.CloseAsync();
            }
            catch { }

            _playwright?.Dispose();
            _initialized = false;
            _initializationAttempted = false;
            _initializationError = null;
        }
    }
}
