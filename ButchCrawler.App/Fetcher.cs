using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public class FetchResult
    {
        public string Url { get; set; } = string.Empty;
        public string FinalUrl { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string RawHtml { get; set; } = string.Empty;
        public bool Success { get; set; }
        public bool? Retryable { get; set; }
        public TimeSpan? RetryAfter { get; set; }
        public string Error { get; set; } = string.Empty;

        public int? StatusCode { get; set; }

        public string? ContentType { get; set; }
        public long? ContentLength { get; set; }
        public bool IsPdf { get; set; }
        public byte[]? BodyBytes { get; set; }
        public string? Extension { get; set; }

        public DateTime CrawlTimestampUtc { get; set; }
        public string? UserAgent { get; set; }
        public string? RemoteIp { get; set; }
        public string? Asn { get; set; }
        public string? Source { get; set; }
        public string? ETag { get; set; }
        public string? LastModified { get; set; }
        public bool NotModified { get; set; }

        public bool IsChallenge { get; set; }
        public string? ChallengeType { get; set; }
        public string? ChallengeSnippet { get; set; }
    }

    public partial class Fetcher
    {
        private readonly HttpClient _http;
        private readonly ILogger<Fetcher> _logger;
        private readonly int _maxContentBytes;
        private readonly TimeSpan _timeout;
        private readonly IConfiguration _config;
        private readonly ICaptchaSolver _captchaSolver;

        private static readonly string DefaultUserAgent =
            "ButchCrawler/1.0";

        private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Guid, FetchResult> _challengeStore = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores = new(StringComparer.OrdinalIgnoreCase);

        private readonly HostConcurrencyLimiter _hostLimiter;

        private static readonly ActivitySource _activitySource = new("ButchCrawler.Fetcher", "1.0");
        private static readonly Meter _meter = new("ButchCrawler.Fetcher", "1.0");
        private static readonly Counter<long> _fetchCounter = _meter.CreateCounter<long>("fetch.requests");
        private static readonly Histogram<double> _fetchDurationMs = _meter.CreateHistogram<double>("fetch.duration_ms");

        public Fetcher(HttpClient http, ILogger<Fetcher> logger, IConfiguration config, ICaptchaSolver captchaSolver)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _captchaSolver = captchaSolver ?? throw new ArgumentNullException(nameof(captchaSolver));

            var timeoutSeconds = _config.GetValue<int>("Crawler:RequestTimeoutSeconds", 120);
            _timeout = TimeSpan.FromSeconds(timeoutSeconds);

            _http.Timeout = Timeout.InfiniteTimeSpan;
            _maxContentBytes = _config.GetValue<int>("Crawler:MaxContentBytes", 100_000_000);

            _http.DefaultRequestHeaders.UserAgent.Clear();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                _config.GetValue<string>("Crawler:UserAgent") ?? DefaultUserAgent);

            _http.DefaultRequestHeaders.Accept.Clear();
            _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            _http.DefaultRequestHeaders.AcceptLanguage.Clear();
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            _hostLimiter = new HostConcurrencyLimiter(
                maxPerHost: _config.GetValue<int>("Crawler:MaxConcurrencyPerHost", 4),
                minDelay: TimeSpan.FromMilliseconds(_config.GetValue<int>("Crawler:MinDelayMs", 500))
            );
        }

        private HttpClient GetClientForOrigin(string origin)
        {
            return _clients.GetOrAdd(origin, o =>
            {
                var maxPerServer = _config.GetValue<int>("Crawler:MaxConnectionsPerServer", 40);

                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = true,
                    UseProxy = false,
                    CookieContainer = new System.Net.CookieContainer(),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    MaxConnectionsPerServer = Math.Max(1, maxPerServer),
                    ConnectTimeout = TimeSpan.FromSeconds(_config.GetValue<int>("Crawler:ConnectTimeoutSeconds", 60)),
                    KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                    KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(10),

                    // <<-- Add this:
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                       | System.Net.DecompressionMethods.Deflate
                                       | System.Net.DecompressionMethods.Brotli
                };

                if (!_config.GetValue<bool>("Crawler:AllowPrivateNetwork", false))
                    handler.ConnectCallback = NetworkAccessPolicy.ConnectAsync;

                var client = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                    DefaultRequestVersion = HttpVersion.Version11
                };
                client.DefaultRequestHeaders.UserAgent.ParseAdd(_config.GetValue<string>("Crawler:UserAgent") ?? DefaultUserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
                client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
                return client;
            });
        }

        public Guid ReportChallengeForHuman(FetchResult challenge)
        {
            var id = Guid.NewGuid();
            if (string.IsNullOrEmpty(challenge.ChallengeSnippet))
            {
                var snippet = string.Empty;
                try
                {
                    snippet = (challenge.RawHtml ?? string.Empty).Length <= 1000 ? challenge.RawHtml ?? string.Empty : (challenge.RawHtml ?? string.Empty).Substring(0, 1000);
                }
                catch { snippet = string.Empty; }
                challenge.ChallengeSnippet = snippet;
            }

            _challengeStore[id] = challenge;
            _logger.LogInformation("Challenge reported {Id} {Url} {Type}", id, challenge.Url, challenge.ChallengeType ?? "(unknown)");
            return id;
        }

        public IReadOnlyList<KeyValuePair<Guid, FetchResult>> ListPendingChallenges() => _challengeStore.ToArray();

        public bool TryGetChallenge(Guid id, out FetchResult? challenge) => _challengeStore.TryGetValue(id, out challenge);

        public bool AcknowledgeChallenge(Guid id)
        {
            if (_challengeStore.TryRemove(id, out var _))
            {
                _logger.LogInformation("Challenge acknowledged {Id}", id);
                return true;
            }
            return false;
        }

        public async Task<bool> ResolveChallengeAsync(Guid id, bool attemptSolver, CancellationToken ct)
        {
            if (!_challengeStore.TryGetValue(id, out var fr))
                return false;

            if (!attemptSolver)
            {
                _logger.LogInformation("ResolveChallengeAsync called for {Id} without solver request.", id);
                return false;
            }

            try
            {
                var result = await _captchaSolver.SolveAsync(fr, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    _challengeStore.TryRemove(id, out var _);
                    _logger.LogInformation("Challenge {Id} solved by solver (solution present={Has})", id, !string.IsNullOrEmpty(result.Solution));
                    return true;
                }

                _logger.LogWarning("Solver did not solve challenge {Id}: {Error}", id, result.Error);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Solver threw while solving challenge {Id}", id);
                return false;
            }
        }

        public async Task<FetchResult> FetchWithHeadersAsync(string url, Dictionary<string, string>? additionalHeaders, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            url = url.Trim().Trim('\'', '"', ',', '‘', '’', '“', '”');

            try
            {
                var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
                var client = GetClientForOrigin(origin);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (additionalHeaders != null)
                {
                    foreach (var kv in additionalHeaders)
                    {
                        try { request.Headers.Remove(kv.Key); } catch { }
                        try { request.Headers.TryAddWithoutValidation(kv.Key, kv.Value); } catch { }
                    }
                }

                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    linkedCts.CancelAfter(_timeout);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);

                    var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                    if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotModified)
                        return await HttpFailureAsync(url, finalUrl, response, linkedCts.Token).ConfigureAwait(false);
                    var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    var contentLength = response.Content.Headers.ContentLength;

                    if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        string? extension = null;
                        try
                        {
                            var dispositionName = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
                            dispositionName = dispositionName?.Trim().Trim('"');
                            extension = !string.IsNullOrWhiteSpace(dispositionName)
                                ? Path.GetExtension(dispositionName)
                                : Path.GetExtension(response.RequestMessage?.RequestUri?.AbsolutePath ?? finalUrl);
                        }
                        catch { }
                        var contentLimit = StructuredDataDetector.Detect(contentType, extension) != null
                            ? Math.Min(_maxContentBytes, Math.Max(1, _config.GetValue<long>("StructuredData:MaxDownloadBytes", 50_000_000)))
                            : _maxContentBytes;
                        if (contentLength.HasValue && contentLength.Value > contentLimit)
                            return new FetchResult { Url = url, FinalUrl = finalUrl, Success = false, Error = $"Response exceeds max {contentLimit} bytes", StatusCode = (int)response.StatusCode };
                        await using var nonHtmlStream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                        using var nonHtmlMs = new MemoryStream();
                        var buf = new byte[8192];
                        var tot = 0;
                        int r;
                        while ((r = await nonHtmlStream.ReadAsync(buf.AsMemory(0, buf.Length), linkedCts.Token).ConfigureAwait(false)) > 0)
                        {
                            var toWrite = r;
                            if (tot + toWrite > contentLimit)
                            {
                                return new FetchResult { Url = url, FinalUrl = finalUrl, Success = false, Error = $"Response exceeds max {contentLimit} bytes", StatusCode = (int)response.StatusCode };
                            }

                            nonHtmlMs.Write(buf, 0, toWrite);
                            tot += toWrite;
                        }

                        sw.Stop();
                        var bodyBytes = nonHtmlMs.ToArray();
                        var fr = new FetchResult
                        {
                            Url = url,
                            FinalUrl = finalUrl,
                            Success = true,
                            StatusCode = (int)response.StatusCode,
                            ContentType = contentType,
                            ContentLength = contentLength,
                            IsPdf = contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase),
                            BodyBytes = bodyBytes,
                            Extension = extension,
                            ETag = GetEtag(response),
                            LastModified = GetLastModified(response)
                        };

                        fr.CrawlTimestampUtc = DateTime.UtcNow;
                        fr.UserAgent = client.DefaultRequestHeaders.UserAgent.ToString();
                        fr.Source = "http";
                        return fr;
                    }

                    if (contentLength.HasValue && contentLength.Value > _maxContentBytes)
                    {
                        sw.Stop();
                        var msg = $"Content-Length {contentLength.Value} > max {_maxContentBytes}";
                        _logger.LogWarning("Skipped {Url} {Msg}", url, msg);
                        return new() { Url = url, FinalUrl = finalUrl, Success = false, Error = msg, StatusCode = (int)response.StatusCode };
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    var buffer = new byte[8192];
                    var total = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token).ConfigureAwait(false)) > 0)
                    {
                        var toWrite = read;
                        if (total + toWrite > _maxContentBytes)
                            {
                                return new FetchResult { Url = url, FinalUrl = finalUrl, Success = false, Error = $"Response exceeds max {_maxContentBytes} bytes", StatusCode = (int)response.StatusCode };
                            }

                        ms.Write(buffer, 0, toWrite);
                        total += toWrite;
                    }

                    var bytes = ms.ToArray();
                    Encoding encoding = Encoding.UTF8;
                    var charset = response.Content.Headers.ContentType?.CharSet;
                    if (!string.IsNullOrWhiteSpace(charset))
                    {
                        try { encoding = Encoding.GetEncoding(charset); } catch { encoding = Encoding.UTF8; }
                    }

                    var html = encoding.GetString(bytes);
                    sw.Stop();

                    // sanitized snippet logging (avoid printing binary/control chars)
                    if (!string.IsNullOrWhiteSpace(contentType) && contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Fetched HTML {Url} ContentType={ContentType} Bytes={Bytes}", url, contentType, bytes.Length);
                    }
                    else
                    {
                        _logger.LogDebug("Fetched non-HTML {Url} ContentType={ContentType} Bytes={Bytes}", url, contentType ?? "(none)", bytes.Length);
                    }

                    // extract title/text from a guaranteed non-null string
                    var challenge = DetectChallenge(response, html, finalUrl);
                    if (challenge.Item1)
                    {
                        var blocked = new FetchResult { Url = url, FinalUrl = finalUrl, Success = false,
                            IsChallenge = true, ChallengeType = challenge.Item2, ChallengeSnippet = challenge.Item3,
                            Error = $"Challenge detected: {challenge.Item2}", StatusCode = (int)response.StatusCode };
                        ReportChallengeForHuman(blocked);
                        return blocked;
                    }
                    var title = ExtractTitle(html ?? string.Empty);
                    var text = ExtractCleanText(html ?? string.Empty);

                    _logger.LogDebug(
                        "Fetched {Url} {Status} {Elapsed}ms Bytes:{Bytes} Title:{Title}",
                        url,
                        (int)response.StatusCode,
                        sw.ElapsedMilliseconds,
                        html?.Length ?? 0,
                        title);

                    var resultFr = new FetchResult
                    {
                        Url = url,
                        FinalUrl = finalUrl,
                        Title = title,
                        Text = text,
                        RawHtml = html ?? string.Empty,
                        Success = true,
                        StatusCode = (int)response.StatusCode,
                        ETag = GetEtag(response),
                        LastModified = GetLastModified(response)
                    };

                    resultFr.CrawlTimestampUtc = DateTime.UtcNow;
                    resultFr.UserAgent = client.DefaultRequestHeaders.UserAgent.ToString();
                    resultFr.Source = "http";
                    return resultFr;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                _logger.LogError("Timeout {Url} {Elapsed}ms", url, sw.ElapsedMilliseconds);
                return new() { Url = url, Success = false, Error = "Timeout" };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Failed {Url} {Elapsed}ms", url, sw.ElapsedMilliseconds);
                return new() { Url = url, Success = false, Error = ExceptionDetails(ex),
                    Retryable = ex is HttpRequestException requestError && requestError.HttpRequestError == HttpRequestError.SecureConnectionError ? false : null };
            }
        }

        public async Task<FetchResult> FetchAsync(string url, CancellationToken ct,
            Func<Uri, CancellationToken, Task<bool>>? allowRedirect = null,
            IReadOnlyDictionary<string, string>? requestHeaders = null)
        {
            var sw = Stopwatch.StartNew();
            url = url.Trim().Trim('\'', '"', ',', '‘', '’', '“', '”');

            try
            {
                _logger.LogDebug("Fetch start {Url}", url);

                var uri = new Uri(url);
                var origin = uri.GetLeftPart(UriPartial.Authority);
                var client = GetClientForOrigin(origin);

                // Queue retries own the overall retry budget.
                var maxAttempts = Math.Clamp(_config.GetValue<int>("Crawler:FetchMaxAttempts", 1), 1, 4);
                var baseDelayMs = 500;

                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    HttpResponseMessage? response = null;
                    try
                    {
                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        attemptCts.CancelAfter(_timeout);

                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        if (requestHeaders != null)
                            foreach (var header in requestHeaders)
                                request.Headers.TryAddWithoutValidation(header.Key, header.Value);

                        var host = uri.Host;
                        var dnsSw = Stopwatch.StartNew();
                        IPAddress[] addrs = Array.Empty<IPAddress>();
                        try
                        {
                            addrs = await Dns.GetHostAddressesAsync(host, attemptCts.Token).ConfigureAwait(false);
                        }
                        catch (Exception dnsEx)
                        {
                            _logger.LogDebug(dnsEx, "DNS lookup failed for {Host} (attempt {Attempt})", host, attempt);
                        }
                        dnsSw.Stop();
                        _logger.LogDebug("DNS {Host} resolved {Count} addrs in {Ms}ms (attempt {Attempt})", host, addrs.Length, dnsSw.ElapsedMilliseconds, attempt);

                        var reqSw = Stopwatch.StartNew();

                        _fetchCounter.Add(1);
                        using var activity = _activitySource.StartActivity("Fetch.Request", ActivityKind.Internal);
                        activity?.SetTag("url", url);
                        activity?.SetTag("origin", origin);
                        activity?.SetTag("host", uri.Host);
                        activity?.SetTag("attempt", attempt);

                        using (_logger.BeginScope(new Dictionary<string, object> { ["Url"] = url, ["Attempt"] = attempt, ["Host"] = uri.Host }))
                        {
                            using (await _hostLimiter.WaitAsync(uri.Host, attemptCts.Token).ConfigureAwait(false))
                            {
                                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
                            }
                            reqSw.Stop();

                            if (response == null)
                                throw new InvalidOperationException("No response received from HttpClient");

                            var statusCodeValue = (int)response.StatusCode;

                            _fetchDurationMs.Record(reqSw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("host", uri.Host));
                            _logger.LogDebug("Fetch done {Url} Status={Status} ElapsedMs={Ms} Attempt={Attempt}", url, statusCodeValue, reqSw.ElapsedMilliseconds, attempt);

                            if (response.StatusCode == (HttpStatusCode)429)
                            {
                                if (attempt == maxAttempts)
                                    return new() { Url = url, Success = false, Retryable = true, RetryAfter = GetRetryAfterDelay(response), Error = "429 Too Many Requests", StatusCode = statusCodeValue };

                                var retryAfter = GetRetryAfterDelay(response);
                                await Task.Delay(retryAfter, ct).ConfigureAwait(false);
                                continue;
                            }

                            if (statusCodeValue >= 500 && statusCodeValue < 600)
                            {
                                if (attempt == maxAttempts)
                                    return new() { Url = url, Success = false, Retryable = true, RetryAfter = GetRetryAfterDelay(response), Error = $"Server error {statusCodeValue}", StatusCode = statusCodeValue };

                                var delay = TimeSpan.FromMilliseconds(baseDelayMs * Math.Pow(2, attempt - 1));
                                await Task.Delay(delay, ct).ConfigureAwait(false);
                                continue;
                            }

                            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                            int redirectCount = 0;
                            while ((int)response.StatusCode >= (int)HttpStatusCode.MultipleChoices
                                   && (int)response.StatusCode < (int)HttpStatusCode.BadRequest
                                   && redirectCount < 10 && response.Headers.Location != null)
                            {
                                redirectCount++;
                                var next = new Uri(response.RequestMessage!.RequestUri!, response.Headers.Location);
                                Console.WriteLine($"[CRAWL-URL] REDIRECT | HTTP={(int)response.StatusCode} | {response.RequestMessage.RequestUri} -> {next}");
                                if ((next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) ||
                                    (allowRedirect != null && !await allowRedirect(next, attemptCts.Token).ConfigureAwait(false)))
                                    return new() { Url = url, FinalUrl = next.AbsoluteUri, Success = false,
                                        StatusCode = (int)response.StatusCode, Retryable = false, Error = "Redirect target rejected by crawl policy" };
                                using var nextReq = new HttpRequestMessage(HttpMethod.Get, next);
                                HttpResponseMessage nextResp;
                                using (await _hostLimiter.WaitAsync(next.Host, attemptCts.Token).ConfigureAwait(false))
                                {
                                    nextResp = await client.SendAsync(nextReq, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token).ConfigureAwait(false);
                                }
                                finalUrl = nextResp.RequestMessage?.RequestUri?.ToString() ?? finalUrl;
                                response.Dispose();
                                response = nextResp;
                                statusCodeValue = (int)response.StatusCode;

                                if (response == null)
                                    throw new InvalidOperationException("No response received during redirect");
                            }

                            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                                return new() { Url = url, FinalUrl = finalUrl, StatusCode = (int)response.StatusCode,
                                    Success = false, Retryable = false, Error = "Redirect did not resolve to content (missing Location or redirect limit reached)" };
                            if (response.StatusCode == HttpStatusCode.NotModified)
                                return new() { Url = url, FinalUrl = finalUrl, Success = true, NotModified = true,
                                    StatusCode = 304, ETag = GetEtag(response), LastModified = GetLastModified(response), Source = "http" };
                            if (!response.IsSuccessStatusCode) return await HttpFailureAsync(url, finalUrl, response, attemptCts.Token).ConfigureAwait(false);
                            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                            var contentLength = response.Content.Headers.ContentLength;

                            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                            {
                                await using var nonHtmlStream = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
                                using var nonHtmlMs = new MemoryStream();
                                var buf = new byte[8192];
                                var tot = 0;
                                int r;
                                while ((r = await nonHtmlStream.ReadAsync(buf.AsMemory(0, buf.Length), attemptCts.Token).ConfigureAwait(false)) > 0)
                                {
                                    var toWrite = r;
                                    if (tot + toWrite > _maxContentBytes)
                            {
                                return new FetchResult { Url = url, FinalUrl = finalUrl, Success = false, Error = $"Response exceeds max {_maxContentBytes} bytes", StatusCode = (int)response.StatusCode };
                            }

                                    nonHtmlMs.Write(buf, 0, toWrite);
                                    tot += toWrite;
                                }

                                var bodyBytes = nonHtmlMs.ToArray();

                                sw.Stop();
                                _logger.LogInformation("Fetched {Url} {Status} {Elapsed}ms NonHtml:{ContentType}", url, statusCodeValue, sw.ElapsedMilliseconds, contentType);

                                string? extension = null;
                                try { extension = Path.GetExtension(response.RequestMessage?.RequestUri?.AbsolutePath ?? finalUrl); } catch { }

                                var fr = new FetchResult
                                {
                                    Url = url,
                                    FinalUrl = finalUrl,
                                    Success = true,
                                    StatusCode = statusCodeValue,
                                    ContentType = contentType,
                                    ContentLength = contentLength,
                                    IsPdf = contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase),
                                    BodyBytes = bodyBytes,
                                    Extension = extension,
                                    ETag = GetEtag(response),
                                    LastModified = GetLastModified(response)
                                };

                                fr.CrawlTimestampUtc = DateTime.UtcNow;
                                fr.UserAgent = _http.DefaultRequestHeaders.UserAgent.ToString();
                                fr.Source = "http";

                                try
                                {
                                    var sniff = string.Empty;
                                    try { sniff = Encoding.UTF8.GetString(bodyBytes, 0, Math.Min(bodyBytes.Length, 4096)); } catch { }

                                    var dc = DetectChallenge(response, sniff, finalUrl);
                                    if (dc.Item1)
                                    {
                                        fr.IsChallenge = true;
                                        fr.ChallengeType = dc.Item2;
                                        fr.ChallengeSnippet = dc.Item3;
                                        fr.Success = false;
                                        fr.Error = $"Challenge detected: {dc.Item2}";
                                        _logger.LogWarning("Challenge detected for {Url}: {Type}", url, dc.Item2);
                                        ReportChallengeForHuman(fr);
                                    }

                                    var host3 = response.RequestMessage?.RequestUri?.Host;
                                    if (!string.IsNullOrWhiteSpace(host3))
                                    {
                                        try { addrs = await Dns.GetHostAddressesAsync(host3, attemptCts.Token).ConfigureAwait(false); } catch { }

                                        var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                            ?? addrs.FirstOrDefault();
                                        if (ip != null) fr.RemoteIp = ip.ToString();

                                        if (_config.GetValue<bool>("Crawler:EnableAsnLookup", false) && fr.RemoteIp != null)
                                        {
                                            try
                                            {
                                                var asnResp = await _http.GetAsync($"https://api.iptoasn.com/v1/as/ip/{fr.RemoteIp}", ct).ConfigureAwait(false);
                                                if (asnResp.IsSuccessStatusCode)
                                                {
                                                    var body = await asnResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                                                    using var doc = JsonDocument.Parse(body);
                                                    if (doc.RootElement.TryGetProperty("as_number", out var asnEl))
                                                    {
                                                        fr.Asn = asnEl.GetInt32().ToString();
                                                    }
                                                    else if (doc.RootElement.TryGetProperty("as_description", out var descEl))
                                                    {
                                                        fr.Asn = descEl.GetString();
                                                    }
                                                }
                                            }
                                            catch { }
                                        }
                                    }
                                }
                                catch { }

                                return fr;
                            }

                            if (contentLength.HasValue && contentLength.Value > _maxContentBytes)
                            {
                                sw.Stop();
                                var msg = $"Content-Length {contentLength.Value} > max {_maxContentBytes}";
                                _logger.LogWarning("Skipped {Url} {Msg}", url, msg);

                                return new() { Url = url, FinalUrl = finalUrl, Success = false, Error = msg, StatusCode = statusCodeValue };
                            }

                            await using var stream = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
                            using var ms = new MemoryStream();
                            var buffer = new byte[8192];
                            var total = 0;
                            int read;

                            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), attemptCts.Token).ConfigureAwait(false)) > 0)
                            {
                                var toWrite = read;
                                if (total + toWrite > _maxContentBytes)
                            {
                                return new FetchResult { Url = url, FinalUrl = finalUrl, Success = false, Error = $"Response exceeds max {_maxContentBytes} bytes", StatusCode = (int)response.StatusCode };
                            }

                                ms.Write(buffer, 0, toWrite);
                                total += toWrite;
                            }

                            var bytes = ms.ToArray();

                            Encoding encoding = Encoding.UTF8;
                            var charset = response.Content.Headers.ContentType?.CharSet;
                            if (!string.IsNullOrWhiteSpace(charset))
                            {
                                try { encoding = Encoding.GetEncoding(charset); } catch { encoding = Encoding.UTF8; }
                            }

                            var html = encoding.GetString(bytes); // bytes built from ConfigureAwait(false) reads above

                            sw.Stop();

                            var dc2 = DetectChallenge(response, html, finalUrl);
                            if (dc2.Item1)
                            {
                                _logger.LogWarning("Challenge detected for {Url}: {Type}", url, dc2.Item2);
                                var fr = new FetchResult
                                {
                                    Url = url,
                                    FinalUrl = finalUrl,
                                    RawHtml = html ?? string.Empty,
                                    Success = false,
                                    IsChallenge = true,
                                    ChallengeType = dc2.Item2,
                                    ChallengeSnippet = dc2.Item3,
                                    Error = $"Challenge detected: {dc2.Item2}",
                                    StatusCode = statusCodeValue,
                                    CrawlTimestampUtc = DateTime.UtcNow,
                                    UserAgent = _http.DefaultRequestHeaders.UserAgent.ToString(),
                                    Source = "http"
                                };

                                ReportChallengeForHuman(fr);

                                return fr;
                            }

                            var title = ExtractTitle(html);
                            var text = ExtractCleanText(html);

                            _logger.LogDebug(
                                "Fetched {Url} {Status} {Elapsed}ms Bytes:{Bytes} Title:{Title}",
                                url,
                                (int)response.StatusCode,
                                sw.ElapsedMilliseconds,
                                html?.Length ?? 0,
                                title);

                            var resultFr = new FetchResult
                            {
                                Url = url,
                                FinalUrl = finalUrl,
                                Title = title,
                                Text = text,
                                RawHtml = html ?? string.Empty,
                                Success = true,
                                StatusCode = statusCodeValue,
                                ETag = GetEtag(response),
                                LastModified = GetLastModified(response)
                            };

                            resultFr.CrawlTimestampUtc = DateTime.UtcNow;
                            resultFr.UserAgent = _http.DefaultRequestHeaders.UserAgent.ToString();
                            resultFr.Source = "http";

                            try
                            {
                                var host4 = response.RequestMessage?.RequestUri?.Host;
                                if (!string.IsNullOrWhiteSpace(host4))
                                {
                                    try { addrs = await Dns.GetHostAddressesAsync(host4, attemptCts.Token).ConfigureAwait(false); } catch { }

                                    var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                        ?? addrs.FirstOrDefault();
                                    if (ip != null) resultFr.RemoteIp = ip.ToString();

                                    if (_config.GetValue<bool>("Crawler:EnableAsnLookup", false) && resultFr.RemoteIp != null)
                                    {
                                        try
                                        {
                                            var asnResp = await _http.GetAsync($"https://api.iptoasn.com/v1/as/ip/{resultFr.RemoteIp}", ct).ConfigureAwait(false);
                                            if (asnResp.IsSuccessStatusCode)
                                            {
                                                var body = await asnResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                                                using var doc = JsonDocument.Parse(body);
                                                if (doc.RootElement.TryGetProperty("as_number", out var asnEl))
                                                {
                                                    resultFr.Asn = asnEl.GetInt32().ToString();
                                                }
                                                else if (doc.RootElement.TryGetProperty("as_description", out var descEl))
                                                {
                                                    resultFr.Asn = descEl.GetString();
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }

                            return resultFr;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
                    {
                        return new() { Url = url, Success = false, Retryable = false,
                            Error = "TLS connection failed: " + ExceptionDetails(ex) };
                    }
                    catch (Exception ex) when (attempt < maxAttempts)
                    {
                        await Task.Delay(baseDelayMs * attempt, ct).ConfigureAwait(false);
                        _logger.LogDebug("[tid:{ThreadId}] Fetch attempt {Attempt} failed for {Url}: {Message}", Environment.CurrentManagedThreadId, attempt, url, ex.GetBaseException().Message);
                        continue;
                    }
                    catch (Exception ex) when (attempt >= maxAttempts)
                    {
                        _logger.LogError(ex, "Final fetch failure for {Url} after {Attempts} attempts", url, attempt);
                        throw;
                    }
                    finally
                    {
                        response?.Dispose();
                    }
                }

                return new() { Url = url, Success = false, Retryable = true, Error = "All retries failed" };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                sw.Stop();
                _logger.LogError("Timeout {Url} {Elapsed}ms", url, sw.ElapsedMilliseconds);
                return new() { Url = url, Success = false, Retryable = true, Error = "Timeout" };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Failed {Url} {Elapsed}ms", url, sw.ElapsedMilliseconds);
                return new() { Url = url, Success = false, Retryable = ex is HttpRequestException or IOException, Error = ExceptionDetails(ex) };
            }
        }

        private static string ExceptionDetails(Exception ex) => string.Join(" → ", ExceptionChain(ex).Select(e => e.Message).Distinct());
        private static IEnumerable<Exception> ExceptionChain(Exception ex)
        { for (Exception? current = ex; current != null; current = current.InnerException) yield return current; }
        private async Task<FetchResult> HttpFailureAsync(string url, string finalUrl, HttpResponseMessage response, CancellationToken ct)
        {
            var result = new FetchResult { Url = url, FinalUrl = finalUrl, Success = false, StatusCode = (int)response.StatusCode,
                Retryable = (int)response.StatusCode is 408 or 425 or 429 || (int)response.StatusCode >= 500,
                Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}" };
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                result.RetryAfter = GetRetryAfterDelay(response);
            // Inspect a bounded error page for real verification screens, never process it as source content.
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[Math.Min(_maxContentBytes, 16384)]; var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                    if (read == 0) break; total += read;
                }
                var challenge = DetectChallenge(response, Encoding.UTF8.GetString(buffer, 0, total), finalUrl);
                if (challenge.Item1)
                {
                    result.IsChallenge = true; result.ChallengeType = challenge.Item2; result.ChallengeSnippet = challenge.Item3;
                    result.Retryable = false; result.Error = $"Challenge detected: {challenge.Item2}";
                    ReportChallengeForHuman(result);
                }
            }
            return result;
        }

        private TimeSpan GetRetryAfterDelay(HttpResponseMessage resp)
        {
            if (resp.Headers.TryGetValues("Retry-After", out var vals))
            {
                var v = vals.FirstOrDefault();
                if (int.TryParse(v, out var seconds))
                    return TimeSpan.FromSeconds(Math.Min(seconds, 3600));
                if (DateTimeOffset.TryParse(v, out var dto))
                {
                    var delay = dto - DateTimeOffset.UtcNow;
                    return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                }
            }
            return TimeSpan.FromSeconds(10);
        }

        private (bool, string, string) DetectChallenge(HttpResponseMessage? resp, string? bodySnippet, string finalUrl)
        {
            var lower = (bodySnippet ?? string.Empty).ToLowerInvariant();
            // A vendor name or an embedded widget alone does not mean access is blocked.
            var visible = ExtractCleanText(bodySnippet ?? string.Empty).ToLowerInvariant();
            var title = ExtractTitle(bodySnippet ?? string.Empty).Trim().ToLowerInvariant();
            bool instruction = visible.Contains("please verify you are a human")
                || visible.Contains("verify you are human") || visible.Contains("human verification")
                || visible.Contains("complete the captcha") || visible.Contains("enter the characters you see")
                || visible.Contains("enter the text shown");
            bool vendor = lower.Contains("turnstile") || lower.Contains("captcha") || lower.Contains("cf-chl-")
                || lower.Contains("/cdn-cgi/challenge-platform/");
            bool challengeHeader = resp != null && resp.Headers.TryGetValues("cf-mitigated", out var mitigation)
                && mitigation.Any(v => v.Equals("challenge", StringComparison.OrdinalIgnoreCase));
            bool blocked = challengeHeader || (vendor && (title is "just a moment..." or "just a moment…" or "attention required! | cloudflare"))
                || (instruction && visible.Length < 2000);
            bool wafResponse = resp?.StatusCode == HttpStatusCode.Forbidden
                && ((resp.Headers.Server?.ToString() ?? "").Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                    || (resp.Headers.Server?.ToString() ?? "").Contains("akamai", StringComparison.OrdinalIgnoreCase));
            if (blocked || (wafResponse && vendor && instruction))
            {
                var type = lower.Contains("recaptcha") ? "recaptcha"
                    : lower.Contains("hcaptcha") || lower.Contains("h-captcha") ? "hcaptcha"
                    : lower.Contains("turnstile") || lower.Contains("cf-chl-") || lower.Contains("/cdn-cgi/challenge-platform/") ? "cloudflare_turnstile"
                    : wafResponse ? "forbidden_waf" : "human_verification";
                return (true, type, ExtractSnippet(bodySnippet, "captcha"));
            }

            return (false, string.Empty, string.Empty);
        }

        private static string ExtractSnippet(string? text, string marker)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            try
            {
                var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return text.Length <= 200 ? text : text.Substring(0, 200);
                var start = Math.Max(0, idx - 60);
                var len = Math.Min(200, text.Length - start);
                return text.Substring(start, len).Replace("\r", " ").Replace("\n", " ").Trim();
            }
            catch { return string.Empty; }
        }

        private static readonly Regex TitleRegex = new("<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static string? GetEtag(HttpResponseMessage response)
        {
            try
            {
                return response.Headers.ETag?.Tag ?? response.Headers.ETag?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string? GetLastModified(HttpResponseMessage response)
        {
            try
            {
                var lm = response.Content?.Headers?.LastModified;
                if (lm.HasValue) return lm.Value.ToString("R");
            }
            catch { }

            try
            {
                if (response.Headers.TryGetValues("Last-Modified", out var vals))
                    return vals.FirstOrDefault();
            }
            catch { }

            return null;
        }
        private static readonly Regex ScriptRegex = new("<script.*?</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StyleRegex = new("<style.*?</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NoscriptRegex = new("<noscript.*?</noscript>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TagRegex = new("<.*?>", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

        private static string ExtractTitle(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            var match = TitleRegex.Match(html);
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        private static string ExtractCleanText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            html = ScriptRegex.Replace(html, "");
            html = StyleRegex.Replace(html, "");
            html = NoscriptRegex.Replace(html, "");
            var text = TagRegex.Replace(html, " ");
            text = WhitespaceRegex.Replace(text, " ").Trim();
            return text;
        }
    }
}
