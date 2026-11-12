using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public partial class CrawlerService : BackgroundService
    {
        private readonly ILogger<CrawlerService> _logger;
        private readonly IConfiguration _config;
        private readonly Fetcher _fetcher;
        private readonly HtmlParser _parser;
        private readonly SqlStorage _storage;
        private readonly RobotsManager _robots;
        private readonly HostRateLimiter _hostRateLimiter;
        private readonly PayloadProcessorRegistry _processorRegistry;
        private readonly IPdfProcessor _pdfProcessor;
        private readonly IEmbeddingIndexQueue _indexQueue;
        private readonly int _maxConcurrency;
        private readonly int _maxPages;
        private readonly int _maxDepth;
        private readonly bool _allowPdf;
        private int _completedPages;
        private int _startedPages;
        private static readonly object _consoleLock = new();

        private static readonly HashSet<string> _skipExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".css", ".js", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".svg",
            ".woff", ".woff2", ".ttf", ".eot", ".otf",
            ".rar", ".7z", ".mp4", ".mp3", ".avi", ".mov", ".webm"
        };

        private readonly IPlaywrightQueue _playwrightQueue;
        private readonly IMetricsService _metrics;
        private readonly CrawlerStartupSignal _startupSignal;
        private readonly int _playwrightFallbackMinText;
        private readonly int _queueFailureMaxAttempts;
        private readonly TimeSpan _queueFailureRetryDelay;
        private readonly HistoricalNewspaperDiscoveryService? _historicalNewspapers;

        public CrawlerService(
            ILogger<CrawlerService> logger,
            IConfiguration config,
            Fetcher fetcher,
            HtmlParser parser,
            SqlStorage storage,
            RobotsManager robots,
            HostRateLimiter hostRateLimiter,
            PayloadProcessorRegistry processorRegistry,
            IPdfProcessor pdfProcessor,
            IEmbeddingIndexQueue indexQueue,
            IPlaywrightQueue playwrightQueue,
            IMetricsService metrics,
            CrawlerStartupSignal startupSignal,
            HistoricalNewspaperDiscoveryService? historicalNewspapers = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _robots = robots ?? throw new ArgumentNullException(nameof(robots));
            _hostRateLimiter = hostRateLimiter ?? throw new ArgumentNullException(nameof(hostRateLimiter));
            _processorRegistry = processorRegistry ?? throw new ArgumentNullException(nameof(processorRegistry));
            _pdfProcessor = pdfProcessor ?? throw new ArgumentNullException(nameof(pdfProcessor));
            _indexQueue = indexQueue ?? throw new ArgumentNullException(nameof(indexQueue));
            _playwrightQueue = playwrightQueue ?? throw new ArgumentNullException(nameof(playwrightQueue));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _startupSignal = startupSignal ?? throw new ArgumentNullException(nameof(startupSignal));
            _historicalNewspapers = historicalNewspapers;

            _maxConcurrency = config.GetValue<int>("Crawler:MaxConcurrency", 4);
            _maxPages = config.GetValue<int>("Crawler:MaxPages", 100_000);
            _maxDepth = config.GetValue<int>("Crawler:MaxDepth", 5);
            _allowPdf = config.GetValue<bool>("Crawler:AllowPdf", false);

            _playwrightFallbackMinText = config.GetValue<int>("Playwright:FallbackMinTextLength", 200);
            _queueFailureMaxAttempts = Math.Max(1, config.GetValue<int>("Crawler:FailureMaxAttempts", config.GetValue<int>("Crawler:MaxAttempts", 3)));
            _queueFailureRetryDelay = TimeSpan.FromSeconds(Math.Max(1, config.GetValue<int>("Crawler:FailureRetryDelaySeconds", 60)));
        }

        // Small DTO returned by non-HTML handler so we can attribute seed using extracted text.
        private sealed record NonHtmlHandleResult(bool Handled, string? Text, string? FinalUrl);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var seedKeyword = _config.GetValue<string>("Crawler:SeedKeyword");
            var pausedChallengeHosts = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var challengeEvidence = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase);

            // Ensure the database schema is compatible before proceeding
            try
            {
                await _storage.EnsureSchemaVersionAsync(1).ConfigureAwait(false);
                await _storage.EnsureSeedHealthSchemaAsync(stoppingToken).ConfigureAwait(false);
                if (_config.GetValue<bool>("StructuredData:Enabled", true))
                    await _storage.EnsureStructuredDataSchemaAsync(stoppingToken).ConfigureAwait(false);
                var retiredAuthenticatedRows = await _storage.RetireRemovedAuthenticatedSiteQueueAsync(stoppingToken).ConfigureAwait(false);
                if (retiredAuthenticatedRows > 0)
                    _logger.LogInformation("Retired {Count} queue rows from the removed authenticated-site feature", retiredAuthenticatedRows);
                foreach (var host in await _storage.GetPausedChallengeHostsAsync(stoppingToken).ConfigureAwait(false))
                    pausedChallengeHosts.TryAdd(host, 0);
                var requeuedHealthChecks = await _storage.RequeueDueSeedHealthChecksAsync(stoppingToken).ConfigureAwait(false);
                if (requeuedHealthChecks > 0)
                    Console.WriteLine($"[CRAWL-URL] QUEUED {requeuedHealthChecks} | Scheduled seed health checks are ready");
                if (_config.GetValue<bool>("Audio:Enabled", false))
                {
                    var recoveredAudio = await _storage.RequeueUnprocessedAudioAsync(stoppingToken).ConfigureAwait(false);
                    if (recoveredAudio > 0) Console.WriteLine($"[CRAWL-URL] QUEUED {recoveredAudio} | Previously skipped audio is ready for transcription");
                }
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Database schema version incompatible or missing. Aborting crawl.");
                _startupSignal.Fail("The database could not be opened or its schema is not ready.");
                return;
            }

            // Keywords describe sought-after CONTENT, not necessarily the repository URL.
            // Do not discard unrelated-looking repository seeds unless explicitly requested.
            List<SeedUrl> seeds = await _storage.GetActiveSeedUrlsAsync();
            var filterSeedsByKeywords = _config.GetValue<bool>("Crawler:FilterSeedsByKeywords", false);
            if (filterSeedsByKeywords && !string.IsNullOrWhiteSpace(seedKeyword))
                seeds = await _storage.GetSeedUrlsByKeywordAsync(seedKeyword);

            // Upsert any configured seed keywords from appsettings into the SeedKeywords table,
            // then load the active keywords from DB and, if any exist, use them to select seeds.
            var configKeywords = _config.GetSection("Crawler:SeedKeywords").Get<List<string>>() ?? new List<string>();
            if (configKeywords.Count > 0)
            {
                try
                {
                    await _storage.UpsertKeywordsAsync(configKeywords, source: "appsettings");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to upsert seed keywords from configuration");
                    try { await _storage.LogErrorAsync(null, $"Failed to upsert seed keywords: {ex}"); } catch { }
                }
            }

            List<string> keywords;
            try { keywords = await _storage.GetKeywordsAsync(throwOnError: true); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to load research keywords; stopping crawl.");
                Console.WriteLine("[CRAWL-ERROR] Could not load research keywords. Check the database connection before restarting.");
                _startupSignal.Fail("Research keywords could not be loaded from the database.");
                return;
            }
            Console.WriteLine($"[CRAWL-KEYWORDS] Loaded {keywords.Count} active research keywords. Matching is case-insensitive literal text matching.");
            if (keywords.Count == 0) Console.WriteLine("[CRAWL-ERROR] No active research keywords are configured. Add or activate keywords in Settings → Seed keywords.");

            if (filterSeedsByKeywords && keywords.Count > 0)
            {
                try
                {
                    var matched = new List<SeedUrl>();
                    foreach (var kw in keywords)
                    {
                        try
                        {
                            var s = await _storage.GetSeedUrlsByKeywordAsync(kw);
                            if (s != null && s.Count > 0) matched.AddRange(s);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to query seed urls for keyword {Keyword}", kw);
                        }
                    }

                    if (matched.Count > 0)
                    {
                        // Deduplicate by URL (case-insensitive)
                        seeds = matched
                            .GroupBy(s => s.Url, StringComparer.Ordinal)
                            .Select(g => g.First())
                            .ToList();
                        _logger.LogInformation("Using {Count} seeds matching SeedKeywords table", seeds.Count);
                    }
                    else
                    {
                        _logger.LogWarning("No active seeds matched keyword filters; using the previously selected active seeds.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed while trying to use SeedKeywords to select seeds");
                }
            }

            _logger.LogInformation("Seed selection: {Count} active seeds; metadata keyword filtering enabled={Enabled}; research keyword count={KeywordCount}", seeds.Count, filterSeedsByKeywords, keywords.Count);

            // Keep the initial queue bounded to the configured page budget. A large seed
            // catalog is not itself a request to enqueue every row on each test run.
            var configuredSeedLimit = _config.GetValue<int>("Crawler:InitialSeedLimit", 0);
            var seedBudget = configuredSeedLimit <= 0
                ? seeds.Count
                : Math.Min(configuredSeedLimit, Math.Max(1, _maxPages));
            seedBudget = Math.Max(1, seedBudget);
            var selectedSeeds = new List<SeedUrl>(seedBudget);
            var startUrls = new List<string>(seedBudget);
            var uniqueUrls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in seeds)
            {
                if (string.IsNullOrWhiteSpace(seed.Url)) continue;
                var normalized = UrlHelper.NormalizeUrl(seed.Url) ?? seed.Url.Trim();
                if (string.IsNullOrWhiteSpace(normalized) || ShouldSkipUrl(normalized) ||
                    normalized.Length > 2000 || !uniqueUrls.Add(normalized)) continue;
                selectedSeeds.Add(seed);
                startUrls.Add(normalized);
                if (startUrls.Count >= seedBudget) break;
            }

            if (startUrls.Count == 0)
            {
                _logger.LogError("No eligible seed URLs found in database. Aborting crawl.");
                _startupSignal.Fail("No eligible seed URLs are configured.");
                return;
            }

            // These parallel arrays preserve seed attribution without O(N^2) lookups.
            var seedMeta = selectedSeeds.Cast<SeedUrl?>().ToList();
            // Exact queue-origin lookup. Never infer collection ownership from a shared host.
            var seedIndexById = selectedSeeds.Select((seed, index) => (seed, index))
                .ToDictionary(x => x.seed.Id, x => x.index);
            var seedIndexByUrl = startUrls.Select((url, index) => (url, index))
                .ToDictionary(x => x.url, x => x.index, StringComparer.Ordinal);

            var seedOutstanding = new int[startUrls.Count];
            var seedSeen = new bool[startUrls.Count];
            var initialEnqueue = new List<EnqueueUrlRequest>(startUrls.Count);
            for (var i = 0; i < startUrls.Count; i++)
            {
                var seed = selectedSeeds[i];
                initialEnqueue.Add(new EnqueueUrlRequest(startUrls[i], SeedId: seed.Id,
                    SeedUrl: seed.Url, ParentUrl: null, Depth: 0));
            }

            _logger.LogInformation("Enqueuing {Selected} root seeds from {Available} active seed records (MaxPages={MaxPages}, SeedUrlLimit={Limit})",
                initialEnqueue.Count, seeds.Count, _maxPages, configuredSeedLimit <= 0 ? "unlimited" : configuredSeedLimit.ToString(CultureInfo.InvariantCulture));
            try
            {
                await _storage.EnqueueUrlsAsync(initialEnqueue).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Initial URL enqueue failed; crawler cannot proceed.");
                _startupSignal.Fail("The initial crawl queue could not be prepared.");
                return;
            }
            var seedProgress = await _storage.GetActiveSeedProgressAsync(stoppingToken).ConfigureAwait(false);
            long successfulSeedUrls = seedProgress.Successful;
            long problemSeedUrls = seedProgress.Problems;
            var totalActiveSeedUrls = seedProgress.Total;
            Console.WriteLine($"[CRAWL-SEEDS] PROGRESS | {successfulSeedUrls} | {problemSeedUrls} | {totalActiveSeedUrls}");
            _startupSignal.Ready();
            var queueState = await _storage.GetPendingAndLockedCountsAsync().ConfigureAwait(false);
            _logger.LogInformation("Initial enqueue finished: Pending={Pending}, Locked={Locked}", queueState.Pending, queueState.Locked);

            // Sitemap discovery can be slow and must not delay the Running signal or
            // ordinary queue processing. It adds results to the same durable queue.
            var sitemapDiscoveryTask = DiscoverAndEnqueueSitemapsAsync(selectedSeeds, uniqueUrls, stoppingToken);
            var newspaperDiscoveryTask = _historicalNewspapers?.DiscoverAndEnqueueAsync(stoppingToken) ?? Task.CompletedTask;

            var persisted = await _storage.GetVisitedUrlsAsync();
            var visited = new ConcurrentDictionary<string, string?>(persisted, StringComparer.Ordinal);

            var depth = new ConcurrentDictionary<string, int>(startUrls.ToDictionary(u => u, _ => 0), StringComparer.Ordinal);

            var running = new List<Task>();
            // A request can legitimately run for the full HTTP timeout before parsing and
            // persistence begin. Keep its claim longer than that so another worker cannot
            // reclaim the same row while the first worker is still finishing it.
            var leaseSeconds = Math.Max(
                300,
                _config.GetValue<int>("Crawler:QueueLeaseSeconds",
                    _config.GetValue<int>("Crawler:RequestTimeoutSeconds", 120) + 120));
            var lease = TimeSpan.FromSeconds(leaseSeconds);

            // Ensure info logging is suppressed for the duration of the crawl loop.
            var previousSuppress = LoggingControl.Current?.SuppressInfo ?? false;
            try
            {
                if (LoggingControl.Current != null)
                    LoggingControl.Current.SuppressInfo = true;

                try
                {
                    while (!stoppingToken.IsCancellationRequested &&
                           Volatile.Read(ref _completedPages) < _maxPages)
                    {
                        for (int i = running.Count - 1; i >= 0; i--)
                        {
                            var t = running[i];
                            if (t.IsCompleted)
                            {
                                running.RemoveAt(i);
                            }
                        }

                        var slots = _maxConcurrency - running.Count;
                        if (slots <= 0)
                        {
                            var completed = await Task.WhenAny(running);
                            continue;
                        }

                        await _storage.ReleaseExpiredLeasesAsync();
                        var claimed = await _storage.ClaimUrlsAsync(slots, lease);
                        _logger.LogDebug("Claimed {Count} urls from storage", claimed.Count);
                        if (claimed.Count > 0)
                        {
                            var sample = string.Join(Environment.NewLine, claimed.Select(c => c.Url).Take(20));
                            _logger.LogInformation("Claimed {Count} urls:\n{Sample}{More}", claimed.Count, sample, claimed.Count > 20 ? "..." : string.Empty);
                        }

                        if (claimed.Count == 0)
                        {
                            if (running.Count == 0)
                            {
                                // No running tasks and nothing claimed now — check DB to see whether there is pending work
                                try
                                {
                                    var (pending, locked) = await _storage.GetPendingAndLockedCountsAsync();
                                    _logger.LogDebug("No claimed urls; pending={Pending}, locked={Locked}", pending, locked);

                                    // If there are pending rows but they are currently locked by leases or other workers,
                                    // wait a bit for leases to expire or for other workers to release them.
                                    if (pending > 0)
                                    {
                                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                                        continue;
                                    }
                                    if (!sitemapDiscoveryTask.IsCompleted || !newspaperDiscoveryTask.IsCompleted)
                                    {
                                        // Discovery services run beside the main queue. Give them a chance
                                        // to add work before deciding this crawl is truly finished.
                                        await Task.WhenAny(Task.WhenAll(sitemapDiscoveryTask, newspaperDiscoveryTask),
                                            Task.Delay(TimeSpan.FromSeconds(2), stoppingToken));
                                        continue;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to query pending/locked counts; assuming no pending work");
                                }

                                // Nothing pending (or query failed) -> exit loop
                                break;
                            }

                            await Task.WhenAny(running).WaitAsync(stoppingToken);
                            continue;
                        }

                        // For each claimed URL start processing (no early seed attribution)
                        foreach (var queued in claimed)
                        {
                            if (stoppingToken.IsCancellationRequested)
                                break;

                            var normUrl = UrlHelper.NormalizeUrl(queued.Url) ?? queued.Url.Trim();

                            // Determine the effective depth for this row (in-memory map takes precedence, then DB depth if present)
                            int queuedDepth = 0;
                            if (!depth.TryGetValue(normUrl, out var tmpDepth))
                            {
                                queuedDepth = queued.Depth ?? 0;
                            }
                            else
                            {
                                queuedDepth = tmpDepth;
                            }

                            var queuedHost = TryGetHost(queued.Url);
                            if (queued.Id.HasValue && queuedHost != null && pausedChallengeHosts.ContainsKey(queuedHost))
                            {
                                if (await _storage.DeferForChallengeHostAsync(queued.Id.Value, queued.LockToken, queuedHost, stoppingToken).ConfigureAwait(false))
                                    Console.WriteLine($"[CRAWL-URL] DEFERRED | Waiting for domain verification ({queuedHost}) | {queued.Url}");
                                continue;
                            }

                            // Determine per-seed max depth if available; fall back to global _maxDepth
                            int maxDepthForThis = _maxDepth;
                            if (queued.SeedId.HasValue)
                            {
                                var s = seeds.FirstOrDefault(x => x.Id == queued.SeedId.Value);
                                if (s != null) maxDepthForThis = s.MaxDepth;
                            }
                            else if (!string.IsNullOrEmpty(queued.SeedUrl))
                            {
                                var s = seeds.FirstOrDefault(x => string.Equals(UrlHelper.NormalizeUrl(x.Url) ?? x.Url.Trim(), queued.SeedUrl, StringComparison.Ordinal) || string.Equals(x.Url, queued.SeedUrl, StringComparison.Ordinal));
                                if (s != null) maxDepthForThis = s.MaxDepth;
                            }

                            if (queuedDepth > maxDepthForThis)
                            {
                                Console.WriteLine($"[CRAWL-URL] SKIPPED | Depth limit {maxDepthForThis} | {queued.Url}");
                                _logger.LogDebug("Skipping {Url} due to max depth {Depth}", queued.Url, queuedDepth);
                                if (queued.Id.HasValue)
                                    await _storage.CompleteUrlAsync(queued.Id.Value, queued.LockToken);
                                else
                                    _logger.LogWarning("Cannot safely complete row for {Url} without queue id", queued.Url);
                                continue;
                            }

                            // A deliberately restarted root seed must be fetched again even
                            // though its prior visit history is preserved. Child duplicates
                            // still use VisitedUrls to avoid recrawling the same discovered page.
                            if (queuedDepth > 0 && visited.ContainsKey(normUrl))
                            {
                                Console.WriteLine($"[CRAWL-URL] SKIPPED | Already visited | {queued.Url}");
                                if (queued.Id.HasValue)
                                    await _storage.CompleteUrlAsync(queued.Id.Value, queued.LockToken);
                                else
                                    _logger.LogWarning("Cannot safely complete duplicate row for {Url} without queue id", queued.Url);
                                continue;
                            }

                            visited.TryAdd(normUrl, null);

                            // keep thisSeedIndex outside the local method so finally can read/decrement it
                            int thisSeedIndex = -1;

                            // Local async method (no Task.Run) — lets the threadpool manage async I/O efficiently.
                            async Task ProcessQueueUrlAsync()
                            {
                                await using var ownership = new QueueLeaseHeartbeat(
                                    () => queued.Id.HasValue && queued.LockToken.HasValue
                                        ? _storage.RenewLeaseAsync(queued.Id.Value, queued.LockToken, lease)
                                        : Task.FromResult(true), stoppingToken);
                                var workToken = ownership.Token;
                                List<string> newlyDiscovered = new List<string>();
                                string resultUrlForDisplay = string.Empty;
                                string headerStatusText = string.Empty;
                                bool headerIsCrawled = false;
                                int displayIndex = Interlocked.Increment(ref _startedPages);
                                string resultUrlNorm = string.Empty;
                                string robotsDecisionTag = "robots=unknown;robotsVersion=rfc9309-lite-v1";
                                bool queueStateFinalized = false;
                                bool rootSeedProgressRecorded = false;

                                // Precompute seed source string from claimed row if available (used even before seed attribution)
                                string? seedSourceStr = null;
                                SeedUrl? seedObjFromQueue = null;
                                if (queued.SeedId.HasValue && seedIndexById.TryGetValue(queued.SeedId.Value, out var indexById))
                                {
                                    thisSeedIndex = indexById;
                                }
                                else if (!string.IsNullOrWhiteSpace(queued.SeedUrl))
                                {
                                    var origin = UrlHelper.NormalizeUrl(queued.SeedUrl) ?? queued.SeedUrl.Trim();
                                    if (seedIndexByUrl.TryGetValue(origin, out var indexByUrl))
                                        thisSeedIndex = indexByUrl;
                                }
                                if (thisSeedIndex >= 0)
                                {
                                    seedObjFromQueue = seedMeta[thisSeedIndex];
                                    Interlocked.Increment(ref seedOutstanding[thisSeedIndex]);
                                    seedSeen[thisSeedIndex] = true;
                                }
                                else
                                {
                                    _logger.LogWarning("Queue row has no matching selected seed origin: {Url} (SeedId={SeedId}, SeedUrl={SeedUrl})",
                                        queued.Url, queued.SeedId, queued.SeedUrl);
                                }
                                if (seedObjFromQueue != null)
                                {
                                    var s = seedObjFromQueue;
                                    seedSourceStr = $"seedId={s.Id};seedUrl={s.Url};domain={s.Domain};category={s.Category};sourceType={s.SourceType};discoverySource={s.DiscoverySource};tags={s.Tags}";
                                }

                                async Task RenewLeaseSafeAsync()
                                {
                                    if (queued.Id.HasValue && queued.LockToken.HasValue)
                                    {
                                        try { await _storage.RenewLeaseAsync(queued.Id.Value, queued.LockToken, lease).ConfigureAwait(false); } catch { }
                                    }
                                }

                                async Task HandleFailureWithRetryAsync(int statusCode, string reason, bool transient, TimeSpan? retryAfter = null)
                                {
                                    if (!queued.Id.HasValue)
                                    {
                                        _logger.LogWarning("Cannot update retry state for {Url} because queue id is unavailable", queued.Url);
                                        return;
                                    }

                                    if (queueStateFinalized || ownership.OwnershipLost)
                                        return;

                                    var maxAttempts = transient ? _queueFailureMaxAttempts : 1;
                                    var retryDelay = retryAfter > _queueFailureRetryDelay ? retryAfter.Value : _queueFailureRetryDelay;
                                    var failureState = await _storage.HandleFetchFailureAsync(
                                        queued.Id.Value,
                                        queued.LockToken,
                                        queued.Url,
                                        statusCode,
                                        reason,
                                        maxAttempts,
                                        retryDelay,
                                        seedSourceStr == null ? robotsDecisionTag : $"{seedSourceStr};{robotsDecisionTag}",
                                        null,
                                        null,
                                        null).ConfigureAwait(false);
                                    if (failureState == "dead-letter" && queuedDepth == 0 && seedObjFromQueue != null)
                                    {
                                        var policy = ClassifySeedFailure(statusCode, reason);
                                        await _storage.RecordSeedFailureAsync(seedObjFromQueue.Id, statusCode, reason,
                                            policy.HealthStatus, policy.Deactivate, policy.NextCheckUtc, workToken).ConfigureAwait(false);
                                        if (policy.Deactivate)
                                            Console.WriteLine($"[CRAWL-URL] QUARANTINED | {policy.HealthStatus} | {queued.Url}");
                                        if (!rootSeedProgressRecorded)
                                        {
                                            rootSeedProgressRecorded = true;
                                            var problems = Interlocked.Increment(ref problemSeedUrls);
                                            Console.WriteLine($"[CRAWL-SEEDS] PROGRESS | {successfulSeedUrls} | {problems} | {totalActiveSeedUrls}");
                                        }
                                    }
                                    queueStateFinalized = true;
                                    visited.TryRemove(normUrl, out _);
                                    var outcome = failureState == "pending" ? "RETRY" : "DEAD-LETTER";
                                    var detail = failureState == "pending" ? $"Next attempt in {retryDelay.TotalSeconds:0}s" : "No automatic retries remaining";
                                    Console.WriteLine($"[CRAWL-URL] FAILURE {outcome} | HTTP={statusCode} | {detail} | {reason.Replace('\r', ' ').Replace('\n', ' ')} | {queued.Url}");
                                }

                                async Task RecordSeedSuccessSafeAsync()
                                {
                                    if (queuedDepth != 0 || seedObjFromQueue == null) return;
                                    try
                                    {
                                        await _storage.RecordSeedSuccessAsync(seedObjFromQueue.Id, workToken).ConfigureAwait(false);
                                        if (!rootSeedProgressRecorded)
                                        {
                                            rootSeedProgressRecorded = true;
                                            var successful = Interlocked.Increment(ref successfulSeedUrls);
                                            Console.WriteLine($"[CRAWL-SEEDS] PROGRESS | {successful} | {problemSeedUrls} | {totalActiveSeedUrls}");
                                        }
                                    }
                                    catch (Exception ex) { _logger.LogWarning(ex, "Could not update seed health for {Url}", queued.Url); }
                                }

                                try
                                {
                                    // Print concise console progress: always show index/host for claimed URLs.
                                    try
                                    {
                                        Console.WriteLine();
                                        var host = queued.Url;
                                        try { host = new Uri(queued.Url).Host; } catch { host = queued.Url; }
                                        Console.WriteLine($"{displayIndex}/{_maxPages}  {host}");
                                    }
                                    catch { }

                                    Console.WriteLine($"[CRAWL-URL] START | Depth={queuedDepth} | {queued.Url}");
                                    _logger.LogInformation("Crawling: {Url}", queued.Url);
                                    await RenewLeaseSafeAsync().ConfigureAwait(false);

                                    // 1) Respect robots allow/disallow
                                    try
                                    {
                                        if (!await _robots.IsAllowedAsync(queued.Url, workToken).ConfigureAwait(false))
                                        {
                                            Console.WriteLine($"[CRAWL-URL] BLOCKED | Robots rules | {queued.Url}");
                                            robotsDecisionTag = _robots.GetDecisionTag(queued.Url);
                                            _logger.LogDebug("Blocked by robots.txt: {Url}", queued.Url);
                                            await HandleFailureWithRetryAsync(403, $"Blocked by robots.txt ({robotsDecisionTag})", transient: false).ConfigureAwait(false);
                                            try { _metrics.RecordCrawl(); } catch { }
                                            return;
                                        }

                                        robotsDecisionTag = _robots.GetDecisionTag(queued.Url);
                                    }
                                    catch (OperationCanceledException) when (workToken.IsCancellationRequested)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex)
                                    {
                                        if (_config.GetValue<bool>("Crawler:RobotsFailClosed", false))
                                            throw new InvalidOperationException("Robots check failed with fail-closed enabled", ex);
                                        _logger.LogWarning(ex, "Robots allow check failed for {Url}; proceeding (fail-open)", queued.Url);
                                    }

                                    // 2) Acquire per-host slot (limits concurrency per-host + enforces a minimum spacing)
                                    IDisposable? hostSlot = null;
                                    try
                                    {
                                        hostSlot = await _hostRateLimiter.AcquireAsync(queued.Url, workToken).ConfigureAwait(false);

                                        // 3) Then ensure robots crawl-delay is respected (robots may require a longer delay than host limiter)
                                        try
                                        {
                                            await _robots.WaitForCrawlDelayAsync(queued.Url, workToken).ConfigureAwait(false);
                                        }
                                        catch (OperationCanceledException) when (workToken.IsCancellationRequested)
                                        {
                                            throw;
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogWarning(ex, "Robots crawl-delay wait failed for {Url}; proceeding", queued.Url);
                                        }

                                        // 4) Fetch now that per-host concurrency and crawl-delay policies are honored
                                        string? redirectRejection = null;
                                        IReadOnlyDictionary<string, string>? conditionalHeaders = null;
                                        if (_config.GetValue<bool>("Crawler:UseConditionalRequests", true))
                                        {
                                            try
                                            {
                                                var prior = await _storage.GetLatestCaptureMetadataAsync(queued.Url).ConfigureAwait(false);
                                                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                                if (!string.IsNullOrWhiteSpace(prior.ETag)) headers["If-None-Match"] = prior.ETag;
                                                else if (!string.IsNullOrWhiteSpace(prior.LastModified)) headers["If-Modified-Since"] = prior.LastModified;
                                                if (headers.Count > 0) conditionalHeaders = headers;
                                            }
                                            catch (Exception ex) { _logger.LogDebug(ex, "Could not load validators for {Url}", queued.Url); }
                                        }

                                        var fetchResult = await _fetcher.FetchAsync(queued.Url, workToken, async (target, token) =>
                                        {
                                            if (!IsUrlAllowedBySeedRules(target.AbsoluteUri, normUrl, seedObjFromQueue))
                                            { redirectRejection = $"Redirect target rejected by crawl policy: seed domain/host scope excludes {target.AbsoluteUri}"; return false; }
                                            if (!await _robots.IsAllowedAsync(target.AbsoluteUri, token).ConfigureAwait(false))
                                            { redirectRejection = $"Redirect target rejected by crawl policy: robots rules ({_robots.GetDecisionTag(target.AbsoluteUri)}) for {target.AbsoluteUri}"; return false; }
                                            await _robots.WaitForCrawlDelayAsync(target.AbsoluteUri, token).ConfigureAwait(false);
                                            return true;
                                        }, conditionalHeaders);

                                        if (fetchResult?.StatusCode is int receivedStatus && receivedStatus > 0)
                                            Console.WriteLine($"[CRAWL-URL] HTTP {receivedStatus} | Depth={queuedDepth} | {queued.Url}");

                                        // A user may have completed a site's human-verification screen from
                                        // the Operator. Retry a detected challenge in an isolated browser using
                                        // that short-lived session. This never blocks the rest of the crawl.
                                        if (fetchResult?.IsChallenge == true && _config.GetValue<bool>("Playwright:Enabled", true) &&
                                            HumanVerificationSessionStore.HasValidSession(queued.Url,
                                                Math.Clamp(_config.GetValue<int>("Playwright:HumanVerificationSessionHours", HumanVerificationSessionStore.DefaultLifetimeHours), 1, 24)))
                                        {
                                            try
                                            {
                                                var renderTimeout = Math.Clamp(_config.GetValue<int>("Playwright:TimeoutMs", 30000), 5000, 120000);
                                                var verified = await _playwrightQueue.EnqueueRenderAsync(queued.Url, renderTimeout,
                                                    saveScreenshot: false, saveTrace: false, workToken).ConfigureAwait(false);
                                                if (verified.Success && !HumanVerificationSessionStore.LooksLikeChallenge(verified.Html, verified.FinalUrl) &&
                                                    IsUrlAllowedBySeedRules(verified.FinalUrl, queued.Url, seedObjFromQueue))
                                                {
                                                    fetchResult = new FetchResult
                                                    {
                                                        Url = queued.Url, FinalUrl = verified.FinalUrl, RawHtml = verified.Html,
                                                        Success = true, StatusCode = verified.StatusCode, ContentType = "text/html",
                                                        CrawlTimestampUtc = DateTime.UtcNow,
                                                        UserAgent = _config.GetValue<string>("Crawler:UserAgent") ?? "ButchCrawler/1.0",
                                                        Source = "human-verified-browser"
                                                    };
                                                    Console.WriteLine($"[CRAWL-URL] VERIFIED | Human-verified browser session accepted | {queued.Url}");
                                                }
                                            }
                                            catch (OperationCanceledException) when (workToken.IsCancellationRequested) { throw; }
                                            catch (Exception ex) { _logger.LogDebug(ex, "Human-verified browser retry failed for {Url}", queued.Url); }
                                        }

                                        if (fetchResult == null || !fetchResult.Success)
                                        {
                                            var reason = redirectRejection ?? fetchResult?.Error ?? "fetch failed";
                                            if (fetchResult?.IsChallenge == true)
                                            {
                                                Console.WriteLine($"[CRAWL-URL] CHALLENGE | {fetchResult.ChallengeType} | {queued.Url}");
                                                await _storage.CreateCaptchaTaskAsync(queued.Url, queued.SeedUrl, fetchResult.ChallengeSnippet,
                                                    fetchResult.StatusCode, fetchResult.ChallengeType, fetchResult.Error).ConfigureAwait(false);
                                                var challengeHost = TryGetHost(queued.Url);
                                                if (challengeHost != null)
                                                {
                                                    var urls = challengeEvidence.GetOrAdd(challengeHost,
                                                        _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                                                    urls.TryAdd(normUrl, 0);
                                                    if (urls.Count >= 2 && pausedChallengeHosts.TryAdd(challengeHost, 0))
                                                    {
                                                        await _storage.PauseChallengeHostAsync(challengeHost, queued.Url, queued.SeedId,
                                                            fetchResult.ChallengeType, workToken).ConfigureAwait(false);
                                                        Console.WriteLine($"[CRAWL-URL] DOMAIN-PAUSED | Two challenged URLs; remaining pages will wait for verification | {challengeHost}");
                                                    }
                                                }
                                            }
                                            else if (reason.Contains("crawl policy", StringComparison.OrdinalIgnoreCase) || reason.Contains("non-public network", StringComparison.OrdinalIgnoreCase))
                                                Console.WriteLine($"[CRAWL-URL] BLOCKED | {reason.Replace('\r', ' ').Replace('\n', ' ')} | {queued.Url}");
                                            var shortReason = reason.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? reason;
                                            headerStatusText = $"Failed: {shortReason}";
                                            headerIsCrawled = false;

                                            // include seed metadata in Source for crawl result
                                            SeedUrl? failedSeed = (thisSeedIndex >= 0 && seedMeta != null && seedMeta.Count > thisSeedIndex)
                                                ? seedMeta[thisSeedIndex]
                                                : null;
                                            if (failedSeed != null)
                                            {
                                                seedSourceStr = $"seedId={failedSeed.Id};seedUrl={failedSeed.Url};domain={failedSeed.Domain};category={failedSeed.Category};sourceType={failedSeed.SourceType};discoverySource={failedSeed.DiscoverySource};tags={failedSeed.Tags}";
                                            }
                                            var failureStatus = fetchResult?.StatusCode ?? 0;
                                            await HandleFailureWithRetryAsync(failureStatus, reason, fetchResult?.Retryable ?? IsTransientStatusCode(failureStatus), fetchResult?.RetryAfter).ConfigureAwait(false);
                                            try { _metrics.RecordCrawl(); } catch { }
                                            return;
                                        }
                                        else
                                        {
                                            if (fetchResult.NotModified)
                                            {
                                                await _storage.AppendCrawlCaptureIfChangedAsync(queued.Url, fetchResult.FinalUrl, "html", 304,
                                                    null, null, null, null, fetchResult.ETag, fetchResult.LastModified, true,
                                                    fetchResult.UserAgent, fetchResult.RemoteIp, fetchResult.Asn, "http;not-modified", DateTime.UtcNow).ConfigureAwait(false);
                                                if (queued.Id.HasValue) await _storage.CompleteUrlAsync(queued.Id.Value, queued.LockToken).ConfigureAwait(false);
                                                queueStateFinalized = queued.Id.HasValue;
                                                headerStatusText = "Unchanged";
                                                headerIsCrawled = true;
                                                Interlocked.Increment(ref _completedPages);
                                                await RecordSeedSuccessSafeAsync().ConfigureAwait(false);
                                                Console.WriteLine($"[CRAWL-URL] UNCHANGED | HTTP 304 | {queued.Url}");
                                                return;
                                            }

                                            var finalUrlCandidate = fetchResult.FinalUrl ?? queued.Url;
                                            var finalUrlNormCandidate = UrlHelper.NormalizeUrl(finalUrlCandidate) ?? finalUrlCandidate;
                                            var queuedNorm = UrlHelper.NormalizeUrl(queued.Url) ?? queued.Url;

                                            if (!string.Equals(finalUrlNormCandidate, queuedNorm, StringComparison.Ordinal))
                                            {
                                                await RenewLeaseSafeAsync().ConfigureAwait(false);

                                                SeedUrl? effectiveSeed = seedObjFromQueue;
                                                if (thisSeedIndex >= 0 && seedMeta != null && seedMeta.Count > thisSeedIndex)
                                                    effectiveSeed = seedMeta[thisSeedIndex];

                                                if (!IsUrlAllowedBySeedRules(finalUrlNormCandidate, queuedNorm, effectiveSeed))
                                                {
                                                    Console.WriteLine($"[CRAWL-URL] BLOCKED | Redirect outside seed scope | {queued.Url}");
                                                    var reason = $"redirect target rejected by seed scope: {finalUrlCandidate}";
                                                    headerStatusText = "Failed: Redirect out of scope";
                                                    headerIsCrawled = false;
                                                    await HandleFailureWithRetryAsync(403, reason, transient: false).ConfigureAwait(false);
                                                    return;
                                                }

                                                if (!await _robots.IsAllowedAsync(finalUrlCandidate, workToken).ConfigureAwait(false))
                                                {
                                                    Console.WriteLine($"[CRAWL-URL] BLOCKED | Redirect blocked by robots | {queued.Url}");
                                                    var reason = $"redirect target blocked by robots: {finalUrlCandidate}";
                                                    robotsDecisionTag = _robots.GetDecisionTag(finalUrlCandidate);
                                                    headerStatusText = "Failed: Redirect blocked by robots";
                                                    headerIsCrawled = false;
                                                    await HandleFailureWithRetryAsync(403, reason, transient: false).ConfigureAwait(false);
                                                    return;
                                                }

                                                // Redirect requests already honored delays in the callback. Reacquiring
                                                // the same-host slot here deadlocks against this worker's own slot.
                                            }

                                            if (AudioProcessor.IsAudio(fetchResult.ContentType, fetchResult.Extension) && !_config.GetValue<bool>("Audio:Enabled", false))
                                            {
                                                Console.WriteLine($"[CRAWL-URL] SKIPPED | Audio transcription disabled | {queued.Url}");
                                                if (queued.Id.HasValue) await _storage.CompleteUrlAsync(queued.Id.Value, queued.LockToken);
                                                queueStateFinalized = queued.Id.HasValue;
                                                await RecordSeedSuccessSafeAsync().ConfigureAwait(false);
                                                return;
                                            }

                                            // Count a successful retrieval independently of extraction or persistence.
                                            if (fetchResult.StatusCode is >= 200 and < 300)
                                                Console.WriteLine($"[CRAWL-URL] RETRIEVED | Depth={queuedDepth} | {queued.Url}");

                                            // Non-HTML: process and get extracted text so we can attempt attribution
                                            if (string.IsNullOrEmpty(fetchResult.RawHtml))
                                            {
                                                string? nonHtmlSeedSource = null;
                                                try
                                                {
                                                    SeedUrl? nonHtmlSeed = null;
                                                    if (queued.SeedId.HasValue)
                                                    {
                                                        nonHtmlSeed = seeds.FirstOrDefault(x => x.Id == queued.SeedId.Value);
                                                    }
                                                    else if (!string.IsNullOrEmpty(queued.SeedUrl))
                                                    {
                                                        nonHtmlSeed = seeds.FirstOrDefault(x =>
                                                            string.Equals(UrlHelper.NormalizeUrl(x.Url) ?? x.Url.Trim(), queued.SeedUrl, StringComparison.Ordinal) ||
                                                            string.Equals(x.Url, queued.SeedUrl, StringComparison.Ordinal));
                                                    }

                                                    if (nonHtmlSeed != null)
                                                    {
                                                        nonHtmlSeedSource = $"seedId={nonHtmlSeed.Id};seedUrl={nonHtmlSeed.Url};domain={nonHtmlSeed.Domain};category={nonHtmlSeed.Category};sourceType={nonHtmlSeed.SourceType};discoverySource={nonHtmlSeed.DiscoverySource};tags={nonHtmlSeed.Tags}";
                                                    }
                                                }
                                                catch { }

                                                var nonHtmlSource = nonHtmlSeedSource == null ? robotsDecisionTag : $"{nonHtmlSeedSource};{robotsDecisionTag}";
                                                var nonHtmlResult = await TryHandleNonHtmlAsync(queued.Id, queued.LockToken, queued.Url, fetchResult, nonHtmlSource, workToken).ConfigureAwait(false);
                                                if (nonHtmlResult.Handled)
                                                {
                                                    if (!fetchResult.IsPdf || _allowPdf)
                                                        Console.WriteLine($"[CRAWL-TYPE] PROCESSED | {ProcessedFileType.Label(fetchResult.ContentType, fetchResult.Extension, StructuredDataDetector.Detect(fetchResult.ContentType, fetchResult.Extension, fetchResult.BodyBytes ?? Array.Empty<byte>()))}");
                                                    queueStateFinalized = true;
                                                    resultUrlForDisplay = nonHtmlResult.FinalUrl ?? queued.Url;
                                                    resultUrlNorm = UrlHelper.NormalizeUrl(resultUrlForDisplay) ?? (resultUrlForDisplay ?? string.Empty).Trim();

                                                    // Preserve the seed provenance supplied by the queue row.

                                                    headerStatusText = "Crawled";
                                                    headerIsCrawled = true;
                                                    await RecordSeedSuccessSafeAsync().ConfigureAwait(false);
                                                    Console.WriteLine($"[CRAWL-URL] COMPLETE | Document processing finished | {queued.Url}");
                                                    return;
                                                }
                                            }

                                            // HTML path. For a successful but nearly empty shell, make one bounded
                                            // browser-rendered attempt. This handles legitimate JavaScript sites while
                                            // retaining the same public-network and robots protections.
                                            var result = _parser.Parse(fetchResult.FinalUrl ?? queued.Url, fetchResult.RawHtml);
                                            if (_config.GetValue<bool>("Playwright:Enabled", true) &&
                                                fetchResult.StatusCode is >= 200 and < 300 &&
                                                (result.Text?.Trim().Length ?? 0) < _playwrightFallbackMinText)
                                            {
                                                try
                                                {
                                                    var renderTimeout = Math.Clamp(_config.GetValue<int>("Playwright:TimeoutMs", 30000), 5000, 120000);
                                                    var rendered = await _playwrightQueue.EnqueueRenderAsync(
                                                        fetchResult.FinalUrl ?? queued.Url, renderTimeout,
                                                        saveScreenshot: false, saveTrace: false, workToken).ConfigureAwait(false);

                                                    if (rendered.Success &&
                                                        IsUrlAllowedBySeedRules(rendered.FinalUrl, queued.Url, seedObjFromQueue) &&
                                                        await _robots.IsAllowedAsync(rendered.FinalUrl, workToken).ConfigureAwait(false))
                                                    {
                                                        fetchResult.FinalUrl = rendered.FinalUrl;
                                                        fetchResult.RawHtml = rendered.Html;
                                                        fetchResult.StatusCode = rendered.StatusCode;
                                                        result = _parser.Parse(rendered.FinalUrl, rendered.Html);
                                                        Console.WriteLine($"[CRAWL-URL] RENDERED | JavaScript content recovered | {queued.Url}");
                                                    }
                                                }
                                                catch (OperationCanceledException) when (workToken.IsCancellationRequested) { throw; }
                                                catch (Exception ex)
                                                {
                                                    _logger.LogDebug(ex, "Browser fallback was unavailable for {Url}", queued.Url);
                                                }
                                            }
                                            try
                                            {
                                                var archiveText = await _storage.GetHistoricalNewspaperMatchTextAsync(queued.Url, workToken).ConfigureAwait(false);
                                                if (!string.IsNullOrWhiteSpace(archiveText) && !(result.Text ?? string.Empty).Contains("[Historical newspaper match]", StringComparison.Ordinal))
                                                {
                                                    result.Text = string.IsNullOrWhiteSpace(result.Text) ? archiveText : result.Text.Trim() + Environment.NewLine + Environment.NewLine + archiveText;
                                                    result.ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Text))).ToLowerInvariant();
                                                }
                                            }
                                            catch (Exception ex) { _logger.LogDebug(ex, "Could not attach historical newspaper metadata for {Url}", queued.Url); }
                                            resultUrlForDisplay = result.Url ?? string.Empty;

                                            var status = fetchResult.StatusCode ?? 0;
                                            if (status >= 400)
                                            {
                                                headerStatusText = $"Failed: {status}";
                                                headerIsCrawled = false;
                                            }
                                            else
                                            {
                                                headerStatusText = "Crawled";
                                                headerIsCrawled = true;
                                            }

                                            // include seed metadata in Source for crawl result
                                            SeedUrl? htmlSeed = (thisSeedIndex >= 0 && seedMeta != null && seedMeta.Count > thisSeedIndex)
                                                ? seedMeta[thisSeedIndex]
                                                : null;
                                            if (htmlSeed != null)
                                            {
                                                seedSourceStr = $"seedId={htmlSeed.Id};seedUrl={htmlSeed.Url};domain={htmlSeed.Domain};category={htmlSeed.Category};sourceType={htmlSeed.SourceType};discoverySource={htmlSeed.DiscoverySource};tags={htmlSeed.Tags}";
                                            }
                                            await _storage.StoreCrawlResultAsync(queued.Url, status, result.Text, DateTime.UtcNow, fetchResult.UserAgent, fetchResult.RemoteIp, fetchResult.Asn, seedSourceStr == null ? robotsDecisionTag : $"{seedSourceStr};{robotsDecisionTag}");

                                            try
                                            {
                                                var jsonLdJson = result.JsonLd != null && result.JsonLd.Count > 0 ? JsonSerializer.Serialize(result.JsonLd) : null;
                                                var openGraphJson = result.OpenGraph != null && result.OpenGraph.Count > 0 ? JsonSerializer.Serialize(result.OpenGraph) : null;
                                                var microformatsJson = result.Microformats != null && result.Microformats.Count > 0 ? JsonSerializer.Serialize(result.Microformats) : null;
                                                var outgoingLinksJson = result.Links != null && result.Links.Count > 0 ? JsonSerializer.Serialize(result.Links) : null;

                                                await _storage.UpsertCrawlArtifactAsync(
                                                    queued.Url,
                                                    result.Url,
                                                    result.Title,
                                                    result.RawHtml,
                                                    jsonLdJson,
                                                    openGraphJson,
                                                    microformatsJson,
                                                    outgoingLinksJson,
                                                    string.IsNullOrWhiteSpace(result.ContentHash) ? null : result.ContentHash,
                                                    null,
                                                    null,
                                                    status,
                                                    fetchResult.UserAgent,
                                                    fetchResult.RemoteIp,
                                                    fetchResult.Asn,
                                                    seedSourceStr == null ? robotsDecisionTag : $"{seedSourceStr};{robotsDecisionTag}",
                                                    DateTime.UtcNow).ConfigureAwait(false);

                                                await _storage.TryAppendCaptureAndAnnotationsAsync(
                                                    queued.Url,
                                                    result.Url,
                                                    "html",
                                                    status,
                                                    result.Text,
                                                    result.RawHtml,
                                                    result.Title,
                                                    string.IsNullOrWhiteSpace(result.ContentHash) ? null : result.ContentHash,
                                                    fetchResult.ETag,
                                                    fetchResult.LastModified,
                                                    status == 304,
                                                    fetchResult.UserAgent,
                                                    fetchResult.RemoteIp,
                                                    fetchResult.Asn,
                                                    seedSourceStr == null ? robotsDecisionTag : $"{seedSourceStr};{robotsDecisionTag}",
                                                    DateTime.UtcNow,
                                                    spans: null).ConfigureAwait(false);
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogWarning(ex, "Failed to persist crawl artifact metadata for {Url}", queued.Url);
                                            }

                                            resultUrlNorm = UrlHelper.NormalizeUrl(result.Url) ?? (result.Url ?? string.Empty).Trim();

                                            // Enqueue HTML page for semantic indexing (embeddings)
                                            try
                                            {
                                                var textToIndex = result.Text ?? string.Empty;
                                                if (_config.GetValue<bool>("Indexer:Enabled", false) && !string.IsNullOrWhiteSpace(textToIndex))
                                                {
                                                    var title = string.IsNullOrWhiteSpace(result.Title) ? null : result.Title;
                                                    var contentHash = string.IsNullOrWhiteSpace(result.ContentHash) ? null : result.ContentHash;
                                                    await _indexQueue.EnqueueAsync(new IndexRequest(result.Url ?? queued.Url, title, textToIndex, contentHash), workToken);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogWarning(ex, "Failed to enqueue HTML page for indexing: {Url}", queued.Url);
                                            }

                                            // Keep original queue seed attribution for HTML and child links.

                                            var extractedLinkCount = result.Links?.Count ?? 0;
                                            var domainRejected = 0;
                                            var hostRejected = 0;
                                            var previouslySeen = 0;
                                            var invalidLinks = 0;
                                            if (result.Links != null && result.Links.Count > 0)
                                            {
                                                // Resolve seed metadata for per-seed crawl rules
                                                SeedUrl? seedObj = null;
                                                if (thisSeedIndex >= 0 && seedMeta != null && seedMeta.Count > thisSeedIndex)
                                                    seedObj = seedMeta[thisSeedIndex];
                                                if (seedObj == null)
                                                    seedObj = seedObjFromQueue;

                                                foreach (var l in result.Links)
                                                {
                                                    try
                                                    {
                                                        var norm = UrlHelper.NormalizeUrl(l);
                                                        if (norm == null) { invalidLinks++; continue; }
                                                        // Enforce per-seed followExternal/domain rules
                                                        if (seedObj != null)
                                                        {
                                                            // If a Domain is specified on the seed, restrict to that domain
                                                            if (!string.IsNullOrWhiteSpace(seedObj.Domain))
                                                            {
                                                                try
                                                                {
                                                                    if (!IsAllowedSeedDomain(seedObj.Domain, norm)) { domainRejected++; continue; }
                                                                }
                                                                catch { domainRejected++; continue; }
                                                            }

                                                            // If FollowExternal is false, restrict to same host as parent
                                                            if (!seedObj.FollowExternal)
                                                            {
                                                                if (!UrlHelper.IsSameHostNormalized(resultUrlNorm, norm)) { hostRejected++; continue; }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            // No seed attribution: default to same-host restriction
                                                            if (!UrlHelper.IsSameHostNormalized(resultUrlNorm, norm)) { hostRejected++; continue; }
                                                        }
                                                        // Discovery is not completion. Marking children visited here
                                                        // makes the claim loop skip them before their first fetch.
                                                        if (visited.ContainsKey(norm) || newlyDiscovered.Contains(norm, StringComparer.Ordinal)) { previouslySeen++; continue; }
                                                        newlyDiscovered.Add(norm);
                                                    }
                                                    catch { }
                                                }
                                            }

                                            // Prepare discovered child queue requests for atomic enqueue with completion.
                                            var enqueueRequests = new List<EnqueueUrlRequest>();
                                            if (newlyDiscovered.Count > 0)
                                            {
                                                var parentNorm = !string.IsNullOrEmpty(resultUrlNorm)
                                                    ? resultUrlNorm
                                                    : (UrlHelper.NormalizeUrl(queued.Url) ?? queued.Url);

                                                if (!depth.TryGetValue(parentNorm, out var parentDepth))
                                                    parentDepth = queued.Depth ?? 0;

                                                var newDepth = parentDepth + 1;

                                                foreach (var nd in newlyDiscovered.OrderByDescending(StructuredDataDetector.IsStructuredLink))
                                                {
                                                    try
                                                    {
                                                        var allowedDepth = _maxDepth;
                                                        SeedUrl? depthSeed = (thisSeedIndex >= 0 && seedMeta != null && seedMeta.Count > thisSeedIndex)
                                                            ? seedMeta[thisSeedIndex]
                                                            : null;
                                                        if (depthSeed != null)
                                                        {
                                                            try { allowedDepth = depthSeed.MaxDepth; } catch { }
                                                        }
                                                        if (newDepth > allowedDepth) continue;
                                                        if (ShouldSkipUrl(nd)) continue;

                                                        depth.TryAdd(nd, newDepth);

                                                        var seedUrl = thisSeedIndex >= 0 ? startUrls[thisSeedIndex] : null;
                                                        var seedId = thisSeedIndex >= 0 && seedMeta != null && seedMeta.Count > thisSeedIndex ? seedMeta[thisSeedIndex]?.Id : null;
                                                        enqueueRequests.Add(new EnqueueUrlRequest(nd, SeedId: seedId, SeedUrl: seedUrl, ParentUrl: parentNorm, Depth: newDepth));
                                                    }
                                                    catch { }
                                                }
                                            }

                                            Console.WriteLine($"[CRAWL-DISCOVERY] Extracted={extractedLinkCount} Eligible={enqueueRequests.Count} DomainRejected={domainRejected} HostRejected={hostRejected} AlreadySeen={previouslySeen} Invalid={invalidLinks} | {queued.Url}");
                                            Console.WriteLine($"[CRAWL-URL] DISCOVERED {enqueueRequests.Count} | Depth={queuedDepth} | {queued.Url}");
                                            foreach (var kw in keywords)
                                            {
                                                var line = KeywordMatchLine(queued.Url, result.Text ?? string.Empty, kw);
                                                if (line != null) Console.WriteLine(line);
                                            }

                                            // Atomically persist HTML result + visited + discovered children + queue completion.
                                            if (queued.Id.HasValue)
                                            {
                                                await RenewLeaseSafeAsync().ConfigureAwait(false);
                                                var insertedChildren = await _storage.CompleteHtmlCrawlAsync(
                                                    queued.Id.Value,
                                                    queued.LockToken,
                                                    queued.Url,
                                                    status,
                                                    result.Text,
                                                    DateTime.UtcNow,
                                                    string.IsNullOrWhiteSpace(result.ContentHash) ? null : result.ContentHash,
                                                    fetchResult.UserAgent,
                                                    fetchResult.RemoteIp,
                                                    fetchResult.Asn,
                                                    seedSourceStr ?? "http",
                                                    enqueueRequests).ConfigureAwait(false);
                                                queueStateFinalized = true;
                                                await RecordSeedSuccessSafeAsync().ConfigureAwait(false);
                                                Console.WriteLine($"[CRAWL-URL] QUEUED {insertedChildren} | Depth={queuedDepth} | {queued.Url}");
                                                Console.WriteLine($"[CRAWL-URL] COMPLETE | HTML processing finished | {queued.Url}");
                                                Console.WriteLine("[CRAWL-TYPE] PROCESSED | HTML");
                                            }
                                            else
                                            {
                                                _logger.LogWarning("Skipping unsafe HTML completion for {Url} because queue id is unavailable", queued.Url);
                                            }

                                            Interlocked.Increment(ref _completedPages);
                                            try { _metrics.RecordCrawl(); } catch { }
                                        }
                                    }
                                    finally
                                    {
                                        // ensure host slot released even on exceptions
                                        try { hostSlot?.Dispose(); } catch { }
                                    }
                                }
                                catch (OperationCanceledException) when (workToken.IsCancellationRequested)
                                {
                                    if (stoppingToken.IsCancellationRequested) throw;
                                    headerStatusText = "Stopped {lease lost}";
                                    _logger.LogWarning("Queue ownership lost for {Url}; stopped without updating another worker's row", queued.Url);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[CRAWL-URL] ERROR | Depth={queuedDepth} | {queued.Url} | {ex.GetType().Name}: {ex.Message.Replace(Environment.NewLine, " ")}");
                                    _logger.LogError(ex, "Error crawling {Url}", queued.Url);
                                    headerStatusText = "Failed {error}";
                                    headerIsCrawled = false;
                                    try
                                    {
                                        var statusCode = (ex is HttpRequestException) ? 503 : 500;
                                        await HandleFailureWithRetryAsync(statusCode, ex.Message, transient: ex is AudioTranscriptionException audioError ? audioError.Retryable : true).ConfigureAwait(false);
                                    }
                                    catch (Exception stateEx)
                                    {
                                        _logger.LogError(stateEx, "Failed to persist retry/dead-letter state for {Url}", queued.Url);
                                    }
                                    try { _metrics.IncrementError(); } catch { }
                                }
                                finally
                                {
                                    // A failed fetch must remain eligible for retry in this run.
                                    if (!queueStateFinalized)
                                        visited.TryRemove(normUrl, out _);
                                    // Print filename or short path and show status.
                                    try
                                    {
                                        string displayName;
                                        if (!string.IsNullOrEmpty(resultUrlForDisplay))
                                        {
                                            try
                                            {
                                                var u = new Uri(resultUrlForDisplay);
                                                var fileName = Path.GetFileName(u.AbsolutePath.TrimEnd('/'));
                                                if (!string.IsNullOrEmpty(fileName))
                                                    displayName = fileName;
                                                else
                                                {
                                                    var seg = u.AbsolutePath?.Trim('/');
                                                    displayName = !string.IsNullOrEmpty(seg) ? seg : u.Host;
                                                }
                                            }
                                            catch
                                            {
                                                displayName = resultUrlForDisplay;
                                            }
                                        }
                                        else
                                        {
                                            // fallback to queued url
                                            displayName = queued.Url;
                                        }

                                        lock (_consoleLock)
                                        {
                                            Console.Write("\t");
                                            Console.Write(displayName);
                                            Console.Write(" ");

                                            if (headerIsCrawled)
                                            {
                                                var prev = Console.ForegroundColor;
                                                try
                                                {
                                                    Console.ForegroundColor = ConsoleColor.Green;
                                                    Console.WriteLine("DONE");
                                                }
                                                finally
                                                {
                                                    Console.ForegroundColor = prev;
                                                }
                                            }
                                            else
                                            {
                                                // headerStatusText contains reason (e.g. "Failed: 404" or fetch error)
                                                Console.WriteLine(headerStatusText);
                                            }

                                            Console.WriteLine();
                                        }
                                    }
                                    catch { }

                                    // per-seed outstanding tracking: decrement and print COMPLETED when outstanding reaches zero.
                                    try
                                    {
                                        if (thisSeedIndex >= 0)
                                        {
                                            var remaining = Interlocked.Decrement(ref seedOutstanding[thisSeedIndex]);
                                            if (remaining <= 0 && seedSeen[thisSeedIndex])
                                            {
                                                // confirm with DB that no pending items remain for this seed (cluster-safe)
                                                try
                                                {
                                                    var pending = await _storage.GetPendingCountForSeedAsync(startUrls[thisSeedIndex]).ConfigureAwait(false);
                                                    if (pending <= 0)
                                                    {
                                                        // NEW BEHAVIOR:
                                                        // - Do NOT print "COMPLETED" suffix.
                                                        // - Print the local file name in GREEN (if available) and the full seed URL in RED.
                                                        var seedUrlStr = startUrls[thisSeedIndex];
                                                        string? fileName = null;
                                                        try
                                                        {
                                                            var u = new Uri(seedUrlStr);
                                                            if (u.IsFile)
                                                            {
                                                                fileName = Path.GetFileName(u.LocalPath);
                                                            }
                                                            else if (u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                                                            {
                                                                // treat loopback/localhost similar to local file for filename extraction
                                                                fileName = Path.GetFileName(u.AbsolutePath);
                                                                if (string.IsNullOrEmpty(fileName)) fileName = null;
                                                            }
                                                        }
                                                        catch
                                                        {
                                                            fileName = null;
                                                        }

                                                        lock (_consoleLock)
                                                        {
                                                            var prev = Console.ForegroundColor;
                                                            try
                                                            {
                                                                Console.Write("\t");
                                                                if (!string.IsNullOrEmpty(fileName))
                                                                {
                                                                    Console.ForegroundColor = ConsoleColor.Green;
                                                                    Console.Write(fileName);
                                                                    Console.ForegroundColor = prev;
                                                                    Console.Write(" ");
                                                                }

                                                                Console.ForegroundColor = ConsoleColor.Red;
                                                                Console.WriteLine(seedUrlStr);
                                                            }
                                                            finally
                                                            {
                                                                Console.ForegroundColor = prev;
                                                            }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        _logger.LogDebug("Seed {Seed} still has {Pending} pending rows in DB; deferring COMPLETED print", startUrls[thisSeedIndex], pending);
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    _logger.LogWarning(ex, "Failed to confirm pending count for seed {Seed}", startUrls[thisSeedIndex]);
                                                    // skip printing to avoid false positives
                                                }
                                            }
                                        }
                                    }
                                    catch { }

                                    // Child URL enqueueing is performed in the HTML success transaction.
                                }
                            }

                            // Start processing without Task.Run; the async Task is added to running to limit concurrency.
                            var task = ProcessQueueUrlAsync();
                            running.Add(task);
                        }
                    }

                    if (running.Count > 0)
                        await Task.WhenAll(running);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Crawl cancelled.");
                }
            }
            finally
            {
                // restore previous logging state
                if (LoggingControl.Current != null)
                    LoggingControl.Current.SuppressInfo = previousSuppress;
            }

            try { await sitemapDiscoveryTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            try { await newspaperDiscoveryTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Historical newspaper discovery ended early; the ordinary crawl completed normally.");
                Console.WriteLine("[CRAWL-NEWSPAPERS] ERROR | Archive search ended early; ordinary crawling continued");
            }

            lock (_consoleLock)
            {
                Console.WriteLine($"Crawl finished. Visited {visited.Count} pages.");
            }
            _logger.LogDebug("Crawl finished. Visited {Count} pages.", visited.Count);
        }

        private static string? TryGetHost(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
            return uri.IdnHost.ToLowerInvariant();
        }

        // Modified to return extracted text and final URL so attribution can occur.
        private async Task<NonHtmlHandleResult> TryHandleNonHtmlAsync(long? queueId, Guid? lockToken, string url, FetchResult fetchResult, string? seedSource, CancellationToken cancellationToken)
        {
            if (fetchResult == null) return new NonHtmlHandleResult(false, null, fetchResult?.FinalUrl ?? url);

            if (fetchResult.IsPdf)
            {
                // Let PDF handler return extracted text
                var handled = await TryHandlePdfAsync(queueId, lockToken, url, fetchResult, seedSource, cancellationToken);
                return new NonHtmlHandleResult(handled, handled ? await _storage.GetLatestContentAsync(url).ConfigureAwait(false) : null, fetchResult.FinalUrl ?? url);
            }

            var processor = _processorRegistry.Resolve(fetchResult.ContentType, fetchResult.Extension);
            if (processor == null || fetchResult.BodyBytes == null || fetchResult.BodyBytes.Length == 0)
                return new NonHtmlHandleResult(false, null, fetchResult.FinalUrl ?? url);

            try
            {
                var processed = processor is AudioProcessor && queueId.HasValue && lockToken.HasValue
                    ? await AudioQueueLeaseGuard.RunAsync(token => processor.ProcessAsync(url, fetchResult.BodyBytes, fetchResult.ContentType, fetchResult.Extension, token),
                        () => _storage.RenewLeaseAsync(queueId.Value, lockToken, TimeSpan.FromSeconds(60)), cancellationToken)
                    : await processor.ProcessAsync(url, fetchResult.BodyBytes, fetchResult.ContentType, fetchResult.Extension, cancellationToken);
                var fullText = processed.Text ?? string.Empty;
                var relevance = await AnalyzeResearchRelevanceAsync(fullText, url, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(relevance.Snippet))
                {
                    _logger.LogDebug("Processed doc {Url} contained no research-vocabulary matches; skipping focused indexing.", url);
                }

                var contentHashHex = BitConverter.ToString(processed.ContentHash).Replace("-", "").ToLowerInvariant();
                await _storage.StoreCrawlResultAsync(url, fetchResult.StatusCode ?? 0, fullText, DateTime.UtcNow, fetchResult.UserAgent, fetchResult.RemoteIp, fetchResult.Asn, seedSource ?? "non-html");
                if (!string.IsNullOrWhiteSpace(processed.StructuredFormat))
                {
                    await _storage.SaveStructuredDataProvenanceAsync(new StructuredDataProvenance(
                        url, processed.StructuredFormat, processed.StructuredCategory ?? "Structured data",
                        fetchResult.BodyBytes.LongLength, processed.StructuredRecordCount, processed.StructuredTableCount,
                        processed.StructuredEntryCount, processed.StructuredTruncated, processed.StructuredWarning,
                        contentHashHex, null), cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"[CRAWL-URL] STRUCTURED | {processed.StructuredFormat}; {(processed.StructuredRecordCount ?? 0):N0} records indexed | {url}");
                }
                await _storage.RecordKeywordHitsAsync(url, fullText).ConfigureAwait(false);
                await LogDocumentKeywordMatchesAsync(url, fullText).ConfigureAwait(false);
                try { await _storage.UpsertVisitedUrlAsync(url, contentHashHex, fetchResult.StatusCode ?? 0, DateTime.UtcNow, fetchResult.UserAgent, fetchResult.RemoteIp, fetchResult.Asn, seedSource ?? null); } catch { }

                await _storage.TryAppendCaptureAndAnnotationsAsync(
                    url,
                    fetchResult.FinalUrl,
                    "non-html",
                    fetchResult.StatusCode,
                    relevance.Snippet,
                    fullText,
                    processed.Title,
                    contentHashHex,
                    fetchResult.ETag,
                    fetchResult.LastModified,
                    (fetchResult.StatusCode ?? 0) == 304,
                    fetchResult.UserAgent,
                    fetchResult.RemoteIp,
                    fetchResult.Asn,
                    seedSource ?? "non-html",
                    DateTime.UtcNow,
                    relevance.Spans).ConfigureAwait(false);

                if (_config.GetValue<bool>("Indexer:Enabled", false) && !string.IsNullOrWhiteSpace(relevance.Snippet))
                {
                    await _indexQueue.EnqueueAsync(new IndexRequest(url, processed.Title, relevance.Snippet, contentHashHex), cancellationToken);
                }

                if (queueId.HasValue)
                    await _storage.CompleteUrlAsync(queueId.Value, lockToken);
                else
                    _logger.LogWarning("No queue id available when completing non-html URL {Url}", url);
                Interlocked.Increment(ref _completedPages);
                try { _metrics.RecordCrawl(); } catch { }

                // return the processed text for attribution
                return new NonHtmlHandleResult(true, processed.Text, fetchResult.FinalUrl ?? url);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (AudioTranscriptionException) { throw; }
            catch (Exception ex)
            {
                if (processor is AudioProcessor)
                    throw new AudioTranscriptionException("Audio processing or transcript storage failed. Check the audio schema and database connection.", true);
                _logger.LogWarning(ex, "Processing failed for {Url}", url);
                throw; // The owning worker records failure/retry; never report failed extraction as COMPLETE.
            }
        }

        private async Task<bool> TryHandlePdfAsync(long? queueId, Guid? lockToken, string url, FetchResult fetchResult, string? seedSource, CancellationToken cancellationToken)
        {
            if (fetchResult == null || !fetchResult.IsPdf)
                return false;

            if (!_allowPdf)
            {
                await _storage.StoreCrawlResultAsync(url, fetchResult.StatusCode ?? 0, string.Empty, DateTime.UtcNow, fetchResult.UserAgent, fetchResult.RemoteIp, fetchResult.Asn, seedSource ?? "pdf");
                if (queueId.HasValue)
                    await _storage.CompleteUrlAsync(queueId.Value, lockToken);
                else
                    _logger.LogWarning("No queue id available when completing PDF URL {Url}", url);
                try { await _storage.UpsertVisitedUrlAsync(url, null, fetchResult.StatusCode ?? 0, DateTime.UtcNow, fetchResult.UserAgent, fetchResult.RemoteIp, fetchResult.Asn, seedSource ?? null); } catch (Exception upEx) { _logger.LogWarning(upEx, "Failed to upsert visited row for PDF skip {Url}", url); }

                return true;
            }

            try
            {
                using var ms = new System.IO.MemoryStream(fetchResult.BodyBytes ?? Array.Empty<byte>());
                var (text, extractedHash) = await _pdfProcessor.ExtractTextAsync(ms, cancellationToken).ConfigureAwait(false);

                var contentHashHex = BitConverter.ToString(extractedHash ?? Array.Empty<byte>()).Replace("-", "").ToLowerInvariant();

                var fullText = text ?? string.Empty;
                var relevance = await AnalyzeResearchRelevanceAsync(fullText, url, cancellationToken).ConfigureAwait(false);

                // Persist canonical crawl result using full extracted text.
                await _storage.StoreCrawlResultAsync(url, fetchResult.StatusCode ?? 0, fullText, DateTime.UtcNow, fetchResult.UserAgent, fetchResult.RemoteIp, fetchResult.Asn, seedSource ?? "pdf");
                await _storage.RecordKeywordHitsAsync(url, fullText).ConfigureAwait(false);
                await LogDocumentKeywordMatchesAsync(url, fullText).ConfigureAwait(false);

                await _storage.UpsertVisitedUrlAsync(url, contentHashHex, fetchResult.StatusCode ?? 0, DateTime.UtcNow, fetchResult.UserAgent, fetchResult.RemoteIp, fetchResult.Asn, seedSource ?? null);

                await _storage.TryAppendCaptureAndAnnotationsAsync(
                    url,
                    fetchResult.FinalUrl,
                    "pdf",
                    fetchResult.StatusCode,
                    relevance.Snippet,
                    fullText,
                    null,
                    contentHashHex,
                    fetchResult.ETag,
                    fetchResult.LastModified,
                    (fetchResult.StatusCode ?? 0) == 304,
                    fetchResult.UserAgent,
                    fetchResult.RemoteIp,
                    fetchResult.Asn,
                    seedSource ?? "pdf",
                    DateTime.UtcNow,
                    relevance.Spans).ConfigureAwait(false);

                if (_config.GetValue<bool>("Indexer:Enabled", false) && !string.IsNullOrWhiteSpace(relevance.Snippet))
                {
                    await _indexQueue.EnqueueAsync(new IndexRequest(url, null, relevance.Snippet, contentHashHex), cancellationToken);
                }
                else
                {
                    _logger.LogDebug("PDF {Url} contained no research-vocabulary matches; skipping focused indexing.", url);
                }

                if (queueId.HasValue)
                    await _storage.CompleteUrlAsync(queueId.Value, lockToken);
                else
                    _logger.LogWarning("No queue id available when completing PDF URL {Url}", url);
                Interlocked.Increment(ref _completedPages);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF processing failed for {Url}", url);
                throw; // Preserve cancellation and let the worker finalize the queue failure once.
            }
        }

        // Analyze text with research vocabulary (names, aliases, places, organizations, events, dates)
        // and return scored match spans plus an optional snippet for indexing.
        private async Task<(string Snippet, List<PdfRelevanceSpan> Spans)> AnalyzeResearchRelevanceAsync(string text, string url, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(text)) return (string.Empty, new List<PdfRelevanceSpan>());

            try
            {
                var vocab = await BuildResearchVocabularyAsync(cancellationToken).ConfigureAwait(false);
                if (vocab.Count == 0)
                {
                    _logger.LogDebug("No research vocabulary available; skipping focused indexing for {Url}", url);
                    return (string.Empty, new List<PdfRelevanceSpan>());
                }

                var tokens = vocab.Select(v => v.Token).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var matches = ButchCrawler.App.Utilities.AhoCorasickSearcher.FindAll(text, tokens, cancellationToken, maxMatches: 500);

                if (matches.Count == 0)
                    return (string.Empty, new List<PdfRelevanceSpan>());

                var vocabByToken = vocab
                    .GroupBy(v => v.Token, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var spans = new List<PdfRelevanceSpan>();
                var windows = new List<(int Start, int End, double Score)>();

                foreach (var m in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!vocabByToken.TryGetValue(m.Pattern, out var term))
                        continue;

                    var start = Math.Max(0, m.Index - 240);
                    var end = Math.Min(text.Length, m.Index + m.Length + 240);
                    var snippetLen = Math.Min(1000, Math.Max(0, end - start));
                    string? snippet = null;
                    if (snippetLen > 0)
                    {
                        snippet = text.Substring(start, snippetLen).Trim();
                        if (snippet.Length > 1000) snippet = snippet.Substring(0, 1000);
                    }

                    spans.Add(new PdfRelevanceSpan(
                        term.Token,
                        term.Category,
                        m.Index,
                        m.Length,
                        term.Score,
                        snippet));

                    windows.Add((start, end, term.Score));
                }

                if (spans.Count == 0)
                    return (string.Empty, spans);

                var merged = windows
                    .OrderBy(w => w.Start)
                    .Aggregate(new List<(int Start, int End, double Score)>(), (list, w) =>
                    {
                        if (list.Count == 0)
                        {
                            list.Add(w);
                            return list;
                        }

                        var last = list[list.Count - 1];
                        if (w.Start <= last.End + 1)
                        {
                            list[list.Count - 1] = (last.Start, Math.Max(last.End, w.End), Math.Max(last.Score, w.Score));
                        }
                        else
                        {
                            list.Add(w);
                        }

                        return list;
                    })
                    .OrderByDescending(w => w.Score)
                    .ThenBy(w => w.Start)
                    .Take(12)
                    .OrderBy(w => w.Start)
                    .ToList();

                var sb = new StringBuilder();
                foreach (var w in merged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var len = Math.Max(0, w.End - w.Start);
                    if (len == 0) continue;
                    var snippet = text.Substring(w.Start, len).Trim();
                    if (string.IsNullOrWhiteSpace(snippet)) continue;
                    if (sb.Length > 0) sb.AppendLine().AppendLine("---").AppendLine();
                    sb.Append(snippet);
                    if (sb.Length >= 24000) break;
                }

                var snippetText = sb.ToString().Trim();
                if (snippetText.Length > 24000)
                    snippetText = snippetText.Substring(0, 24000).Trim();

                return (snippetText, spans);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Research relevance analysis failed for {Url}", url);
                return (string.Empty, new List<PdfRelevanceSpan>());
            }
        }

        private sealed record ResearchTerm(string Token, string Category, double Score);

        private async Task<List<ResearchTerm>> BuildResearchVocabularyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var terms = new List<ResearchTerm>
            {
                new("butch cassidy", "name", 1.0),
                new("robert leroy parker", "name", 1.0),
                new("sundance kid", "name", 1.0),
                new("harry longabaugh", "name", 1.0),
                new("etta place", "name", 0.95),
                new("wild bunch", "organization", 0.95),
                new("hole-in-the-wall", "place", 0.9),
                new("hole in the wall", "place", 0.9),
                new("brown's park", "place", 0.85),
                new("browns park", "place", 0.85),
                new("san vicente", "place", 0.85),
                new("union pacific", "organization", 0.8),
                new("pinkerton", "organization", 0.8),
                new("train robbery", "event", 0.8),
                new("bank robbery", "event", 0.8),
                new("posse", "event", 0.65),
                new("outlaw", "event", 0.6)
            };

            try
            {
                var keywords = await _storage.GetKeywordsAsync().ConfigureAwait(false);
                foreach (var kw in keywords)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(kw)) continue;
                    var token = kw.Trim().ToLowerInvariant();
                    if (token.Length < 3) continue;
                    if (terms.Any(t => string.Equals(t.Token, token, StringComparison.OrdinalIgnoreCase))) continue;
                    terms.Add(new ResearchTerm(token, "keyword", 0.7));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed loading keyword-based research vocabulary.");
            }

            try
            {
                var years = new List<string>();
                for (var y = 1880; y <= 1930; y++) years.Add(y.ToString(CultureInfo.InvariantCulture));
                foreach (var y in years)
                {
                    if (terms.Any(t => string.Equals(t.Token, y, StringComparison.OrdinalIgnoreCase))) continue;
                    terms.Add(new ResearchTerm(y, "date", 0.45));
                }
            }
            catch { }

            return terms
                .Where(t => !string.IsNullOrWhiteSpace(t.Token))
                .GroupBy(t => t.Token, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Score).First())
                .ToList();
        }

        private async Task LogDocumentKeywordMatchesAsync(string url, string text)
        {
            foreach (var keyword in await _storage.GetKeywordsAsync(throwOnError: true).ConfigureAwait(false))
            {
                var line = KeywordMatchLine(url, text, keyword);
                if (line != null) Console.WriteLine(line);
            }
        }

        public static string? KeywordMatchLine(string url, string content, string keyword)
        {
            var count = CountLiteralOccurrences(content, keyword);
            return count == 0 ? null : $"[CRAWL-MATCH] {keyword.Replace('|', ' ').Replace('\r', ' ').Replace('\n', ' ')} ({count}) | {url}";
        }

        private static int CountLiteralOccurrences(string content, string term)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(term)) return 0;
            int count = 0, offset = 0;
            while ((offset = content.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                offset += Math.Max(term.Length, 1);
            }
            return count;
        }

        private sealed record SitemapDocument(bool IsIndex, IReadOnlyList<string> Locations)
        {
            public static SitemapDocument Empty { get; } = new(false, Array.Empty<string>());
        }

        private async Task DiscoverAndEnqueueSitemapsAsync(IReadOnlyList<SeedUrl> selectedSeeds,
            IReadOnlySet<string> rootUrls, CancellationToken ct)
        {
            if (!_config.GetValue<bool>("Crawler:DiscoverSitemaps", true)) return;
            var budget = Math.Min(Math.Max(0, _config.GetValue<int>("Crawler:SitemapUrlLimit", 5000)),
                Math.Max(0, _maxPages - Math.Min(rootUrls.Count, _maxPages)));
            var seedLimit = Math.Max(0, _config.GetValue<int>("Crawler:SitemapSeedLimit", 100));
            if (budget == 0 || seedLimit == 0) return;

            var discoveredUrls = new HashSet<string>(StringComparer.Ordinal);
            var documentCache = new Dictionary<string, SitemapDocument>(StringComparer.Ordinal);
            try
            {
                foreach (var seed in selectedSeeds.Take(seedLimit))
                {
                    ct.ThrowIfCancellationRequested();
                    if (discoveredUrls.Count >= budget) break;
                    var found = await DiscoverSitemapUrlsAsync(seed, budget - discoveredUrls.Count, documentCache, ct).ConfigureAwait(false);
                    var requests = new List<EnqueueUrlRequest>();
                    foreach (var url in found)
                        if (!rootUrls.Contains(url) && discoveredUrls.Add(url))
                            requests.Add(new EnqueueUrlRequest(url, seed.Id, seed.Url, seed.Url, 1));
                    if (requests.Count > 0) await _storage.EnqueueUrlsAsync(requests).ConfigureAwait(false);
                }
                if (discoveredUrls.Count > 0)
                    Console.WriteLine($"[CRAWL-DISCOVERY] Added {discoveredUrls.Count} URLs from declared sitemaps");
                _logger.LogInformation("Sitemap discovery finished: {Urls} URLs from {Files} unique sitemap files",
                    discoveredUrls.Count, documentCache.Count);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Background sitemap discovery stopped unexpectedly"); }
        }

        private async Task<List<string>> DiscoverSitemapUrlsAsync(SeedUrl seed, int limit,
            Dictionary<string, SitemapDocument> documentCache, CancellationToken ct)
        {
            var discovered = new List<string>();
            if (limit <= 0) return discovered;

            var pending = new Queue<string>(await _robots.GetSitemapsAsync(seed.Url, ct).ConfigureAwait(false));
            var seenMaps = new HashSet<string>(StringComparer.Ordinal);
            var maxMaps = Math.Max(1, _config.GetValue<int>("Crawler:SitemapFileLimitPerSeed", 20));

            while (pending.Count > 0 && seenMaps.Count < maxMaps && discovered.Count < limit)
            {
                var sitemapUrl = pending.Dequeue();
                var normalizedMap = UrlHelper.NormalizeUrl(sitemapUrl) ?? sitemapUrl;
                if (!seenMaps.Add(normalizedMap) || !IsUrlAllowedBySeedRules(normalizedMap, seed.Url, seed)) continue;
                if (!documentCache.TryGetValue(normalizedMap, out var sitemap))
                {
                    sitemap = SitemapDocument.Empty;
                    if (await _robots.IsAllowedAsync(normalizedMap, ct).ConfigureAwait(false))
                    {
                        await _robots.WaitForCrawlDelayAsync(normalizedMap, ct).ConfigureAwait(false);
                        var fetched = await _fetcher.FetchAsync(normalizedMap, ct, async (target, token) =>
                        {
                            if (!IsUrlAllowedBySeedRules(target.AbsoluteUri, seed.Url, seed)) return false;
                            if (!await _robots.IsAllowedAsync(target.AbsoluteUri, token).ConfigureAwait(false)) return false;
                            await _robots.WaitForCrawlDelayAsync(target.AbsoluteUri, token).ConfigureAwait(false);
                            return true;
                        }).ConfigureAwait(false);
                        if (fetched.IsChallenge)
                            await _storage.CreateCaptchaTaskAsync(normalizedMap, seed.Url, fetched.ChallengeSnippet,
                                fetched.StatusCode, fetched.ChallengeType, fetched.Error).ConfigureAwait(false);
                        if (fetched.Success)
                        {
                            try
                            {
                                var xml = await DecodeSitemapXmlAsync(fetched, ct).ConfigureAwait(false);
                                var document = XDocument.Parse(xml, LoadOptions.None);
                                var isIndex = string.Equals(document.Root?.Name.LocalName, "sitemapindex", StringComparison.OrdinalIgnoreCase);
                                var locations = document.Descendants()
                                    .Where(x => x.Name.LocalName == "loc" && x.Parent != null &&
                                        (x.Parent.Name.LocalName == "url" || x.Parent.Name.LocalName == "sitemap"))
                                    .Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToArray();
                                sitemap = new SitemapDocument(isIndex, locations);
                            }
                            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.Xml.XmlException)
                            { _logger.LogDebug(ex, "Sitemap could not be decoded or parsed: {Url}", normalizedMap); }
                        }
                    }
                    documentCache[normalizedMap] = sitemap;
                }

                foreach (var location in sitemap.Locations)
                {
                    var normalized = UrlHelper.NormalizeUrl(location);
                    if (normalized == null) continue;
                    if (sitemap.IsIndex)
                    {
                        if (pending.Count + seenMaps.Count < maxMaps) pending.Enqueue(normalized);
                    }
                    else if (!ShouldSkipUrl(normalized) && IsUrlAllowedBySeedRules(normalized, seed.Url, seed))
                    {
                        discovered.Add(normalized);
                        if (discovered.Count >= limit) break;
                    }
                }
            }
            return discovered.Distinct(StringComparer.Ordinal).Take(limit).ToList();
        }

        private static async Task<string> DecodeSitemapXmlAsync(FetchResult fetched, CancellationToken ct)
        {
            const int maxExpandedBytes = 25_000_000;
            if (!string.IsNullOrWhiteSpace(fetched.RawHtml))
            {
                if (Encoding.UTF8.GetByteCount(fetched.RawHtml) > maxExpandedBytes)
                    throw new InvalidDataException("Sitemap exceeds the secure expanded-size limit.");
                return fetched.RawHtml;
            }
            var bytes = fetched.BodyBytes ?? Array.Empty<byte>();
            if (bytes.Length == 0) return string.Empty;
            if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                await using var compressed = new MemoryStream(bytes, writable: false);
                await using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: false);
                using var expanded = new MemoryStream();
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await gzip.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                    if (read == 0) break;
                    if (expanded.Length + read > maxExpandedBytes)
                        throw new InvalidDataException("Compressed sitemap expands beyond the secure size limit.");
                    expanded.Write(buffer, 0, read);
                }
                bytes = expanded.ToArray();
            }
            if (bytes.Length > maxExpandedBytes) throw new InvalidDataException("Sitemap exceeds the secure size limit.");
            return Encoding.UTF8.GetString(bytes);
        }

        private bool ShouldSkipUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                var ext = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(ext))
                {
                    if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        return !_allowPdf;
                    }

                    return _skipExtensions.Contains(ext);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSameHost(string originUrl, string link)
        {
            try
            {
                var o = new Uri(originUrl);
                var l = new Uri(link);
                return string.Equals(o.Host, l.Host, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsTransientStatusCode(int? statusCode)
        {
            // FetchResult has no status for transport failures. The persistence path
            // represents that absence as 0, so both forms must remain retryable.
            if (!statusCode.HasValue || statusCode.Value == 0) return true;
            var code = statusCode.Value;
            if (code == 408 || code == 425 || code == 429) return true;
            return code >= 500 && code <= 599;
        }

        private sealed record SeedFailurePolicy(string HealthStatus, bool Deactivate, DateTime? NextCheckUtc);

        private static SeedFailurePolicy ClassifySeedFailure(int statusCode, string reason)
        {
            var now = DateTime.UtcNow;
            if (reason.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("human verification", StringComparison.OrdinalIgnoreCase))
                return new("human-verification", false, null);
            if (statusCode == 410)
                return new("gone", false, null);
            if (statusCode == 401)
                return new("sign-in-required", false, null);
            if (reason.Contains("robots", StringComparison.OrdinalIgnoreCase))
                return new("robots-blocked", false, null);
            if (statusCode == 403 || reason.Contains("crawl policy", StringComparison.OrdinalIgnoreCase))
                return new("blocked", false, null);
            if (statusCode == 404)
                return new("not-found", false, now.AddDays(7));
            if (statusCode == 429)
                return new("rate-limited", false, now.AddDays(1));
            if (statusCode == 0 || statusCode is 408 or 425 || statusCode >= 500)
                return new("temporarily-unavailable", false, now.AddDays(1));
            return new("problem", false, now.AddDays(7));
        }

        // SeedUrls.Domain normally stores a bare hostname (e.g. archives.example.org),
        // whereas IsSameHostNormalized requires absolute URLs. Compare hosts explicitly.
        private static bool IsAllowedSeedDomain(string configuredDomain, string candidateUrl)
        {
            if (!Uri.TryCreate(candidateUrl, UriKind.Absolute, out var candidate) ||
                (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps))
                return false;

            var domain = configuredDomain.Trim();
            if (!Uri.TryCreate(domain, UriKind.Absolute, out var origin))
            {
                if (!Uri.TryCreate("https://" + domain.TrimStart('/'), UriKind.Absolute, out origin))
                    return false;
            }
            return UrlHelper.HostsMatch(origin.Host, candidate.Host);
        }

        private static bool IsUrlAllowedBySeedRules(string finalUrlNorm, string parentUrlNorm, SeedUrl? seed)
        {
            if (string.IsNullOrWhiteSpace(finalUrlNorm)) return false;

            if (seed != null)
            {
                if (!string.IsNullOrWhiteSpace(seed.Domain))
                {
                    try
                    {
                        if (!IsAllowedSeedDomain(seed.Domain, finalUrlNorm))
                            return false;
                    }
                    catch
                    {
                        return false;
                    }
                }

                if (!seed.FollowExternal)
                {
                    if (!UrlHelper.IsSameHostNormalized(parentUrlNorm, finalUrlNorm))
                        return false;
                }

                return true;
            }

            return UrlHelper.IsSameHostNormalized(parentUrlNorm, finalUrlNorm);
        }
    }
}
