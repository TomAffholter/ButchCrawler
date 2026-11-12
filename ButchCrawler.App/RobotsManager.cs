using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    internal sealed class RobotRule
    {
        public bool Allow { get; init; }
        public string Pattern { get; init; } = string.Empty;
    }

    internal sealed class RobotRules
    {
        public List<RobotRule> Rules { get; } = new();
        public List<string> Sitemaps { get; } = new();
        public double? CrawlDelaySeconds { get; set; }
        public DateTime FetchedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string ParserVersion { get; set; } = "rfc9309-lite-v1";
    }

    internal sealed class RobotGroup
    {
        public List<string> Agents { get; } = new();
        public List<RobotRule> Rules { get; } = new();
        public double? CrawlDelaySeconds { get; set; }
    }

    public class RobotsManager : IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly ILogger<RobotsManager> _logger;
        private readonly ConcurrentDictionary<string, RobotRules> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostLocks = new();
        private readonly ConcurrentDictionary<string, DateTime> _nextRequestUtc = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _decisionTags = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _robotsUserAgent;
        private readonly bool _failClosed;
        private readonly TimeSpan _cacheTtl;

        public RobotsManager(HttpClient? http, ILogger<RobotsManager> logger, IConfiguration config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (config == null) throw new ArgumentNullException(nameof(config));
            var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
            if (!config.GetValue<bool>("Crawler:AllowPrivateNetwork", false))
                handler.ConnectCallback = NetworkAccessPolicy.ConnectAsync;
            _http = http ?? new HttpClient(handler);
            _ownsHttp = http == null;
            if (http != null) handler.Dispose();

            _robotsUserAgent = config.GetValue<string>("Crawler:RobotsUserAgent")
                               ?? config.GetValue<string>("Crawler:UserAgent")
                               ?? "*";
            _failClosed = config.GetValue<bool>("Crawler:RobotsFailClosed", true);
            _cacheTtl = TimeSpan.FromMinutes(Math.Max(1, config.GetValue<int>("Crawler:RobotsCacheMinutes", 720)));
        }

        public string GetDecisionTag(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "robots=unknown;robotsVersion=rfc9309-lite-v1";
            return _decisionTags.TryGetValue(url, out var tag)
                ? tag
                : "robots=unknown;robotsVersion=rfc9309-lite-v1";
        }

        // Returns true if URL is allowed to be crawled
        public async Task<bool> IsAllowedAsync(string url, CancellationToken ct)
        {
            var uri = new Uri(url);
            var origin = GetOrigin(uri);
            var rules = await EnsureLoadedAsync(origin, ct).ConfigureAwait(false);

            if (rules == null)
            {
                var allowedByFailure = !_failClosed;
                _decisionTags[url] = $"robots={(allowedByFailure ? "allow" : "disallow")};robotsVersion=rfc9309-lite-v1;reason=load-failed";
                return allowedByFailure;
            }

            var pathAndQuery = uri.PathAndQuery;
            var matched = FindBestRuleMatch(rules.Rules, pathAndQuery);
            var allowed = matched == null || matched.Allow;

            _decisionTags[url] = $"robots={(allowed ? "allow" : "disallow")};robotsVersion={rules.ParserVersion};rule={(matched?.Pattern ?? "none")}";
            return allowed;
        }

        public async Task<IReadOnlyList<string>> GetSitemapsAsync(string url, CancellationToken ct)
        {
            var uri = new Uri(url);
            var rules = await EnsureLoadedAsync(GetOrigin(uri), ct).ConfigureAwait(false);
            return rules?.Sitemaps.ToArray() ?? Array.Empty<string>();
        }

        // Waits until the host is allowed by crawl-delay, then updates next allowed time
        public async Task WaitForCrawlDelayAsync(string url, CancellationToken ct)
        {
            var uri = new Uri(url);
            var origin = GetOrigin(uri);
            var rules = await EnsureLoadedAsync(origin, ct).ConfigureAwait(false);

            double delaySeconds = 0;
            if (rules?.CrawlDelaySeconds.HasValue == true)
                delaySeconds = Math.Max(0, rules.CrawlDelaySeconds.Value);

            if (delaySeconds <= 0)
                return;

            var now = DateTime.UtcNow;
            var next = _nextRequestUtc.GetOrAdd(origin, DateTime.MinValue);
            if (now < next)
            {
                var wait = next - now;
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _nextRequestUtc[origin] = DateTime.UtcNow.AddSeconds(delaySeconds);
        }

        private async Task<RobotRules?> EnsureLoadedAsync(string origin, CancellationToken ct)
        {
            if (_cache.TryGetValue(origin, out var existing) && existing.ExpiresAtUtc > DateTime.UtcNow)
                return existing;

            var sem = _hostLocks.GetOrAdd(origin, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cache.TryGetValue(origin, out existing) && existing.ExpiresAtUtc > DateTime.UtcNow)
                    return existing;

                var robotsUrl = origin.TrimEnd('/') + "/robots.txt";
                try
                {
                    HttpResponseMessage? resp = null;
                    var currentRobotsUrl = robotsUrl;
                    for (var redirect = 0; redirect <= 5; redirect++)
                    {
                        resp?.Dispose();
                        resp = await _http.GetAsync(currentRobotsUrl, ct).ConfigureAwait(false);
                        if ((int)resp.StatusCode < 300 || (int)resp.StatusCode >= 400 || resp.Headers.Location == null) break;
                        if (redirect == 5) { resp.Dispose(); return null; }
                        var next = new Uri(new Uri(currentRobotsUrl), resp.Headers.Location);
                        if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                        { resp.Dispose(); return null; }
                        currentRobotsUrl = next.AbsoluteUri;
                    }
                    using (resp)
                    {
                    if (resp == null) return null;
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Missing robots files permit access; server/rate-limit failures use
                        // the configured failure policy and must not become cached allow-all.
                        if ((int)resp.StatusCode >= 500 || (int)resp.StatusCode == 429 || (int)resp.StatusCode < 400)
                            return null;
                        var allowAll = new RobotRules
                        {
                            FetchedAtUtc = DateTime.UtcNow,
                            ExpiresAtUtc = DateTime.UtcNow.Add(_cacheTtl)
                        };
                        _cache[origin] = allowAll;
                        return allowAll;
                    }

                    var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var parsed = ParseRobotsTxt(content, _robotsUserAgent);
                    parsed.FetchedAtUtc = DateTime.UtcNow;
                    parsed.ExpiresAtUtc = DateTime.UtcNow.Add(_cacheTtl);
                    _cache[origin] = parsed;
                    return parsed;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch/parse robots.txt for {RobotsUrl}", robotsUrl);
                    return null;
                }
            }
            finally
            {
                sem.Release();
            }
        }

        private static RobotRules ParseRobotsTxt(string content, string userAgent)
        {
            var groups = new List<RobotGroup>();
            var sitemaps = new List<string>();
            RobotGroup? current = null;
            bool sawDirectiveInGroup = false;

            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var commentIdx = line.IndexOf('#');
                if (commentIdx >= 0) line = line.Substring(0, commentIdx).Trim();
                if (line.Length == 0) continue;

                var idx = line.IndexOf(':');
                if (idx <= 0) continue;

                var key = line.Substring(0, idx).Trim();
                var val = line.Substring(idx + 1).Trim();

                if (key.Equals("Sitemap", StringComparison.OrdinalIgnoreCase))
                {
                    if (Uri.TryCreate(val, UriKind.Absolute, out var sitemap) &&
                        (sitemap.Scheme == Uri.UriSchemeHttp || sitemap.Scheme == Uri.UriSchemeHttps))
                        sitemaps.Add(sitemap.AbsoluteUri);
                    continue;
                }

                if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    if (current == null || sawDirectiveInGroup)
                    {
                        current = new RobotGroup();
                        groups.Add(current);
                        sawDirectiveInGroup = false;
                    }

                    if (!string.IsNullOrWhiteSpace(val))
                        current.Agents.Add(val);
                    continue;
                }

                if (current == null)
                    continue;

                sawDirectiveInGroup = true;

                if (key.Equals("Allow", StringComparison.OrdinalIgnoreCase))
                {
                    current.Rules.Add(new RobotRule { Allow = true, Pattern = val ?? string.Empty });
                }
                else if (key.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
                {
                    current.Rules.Add(new RobotRule { Allow = false, Pattern = val ?? string.Empty });
                }
                else if (key.Equals("Crawl-delay", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(val, out var d) && d >= 0)
                        current.CrawlDelaySeconds = d;
                }
            }

            var selected = SelectBestGroups(groups, userAgent);
            var ret = new RobotRules();
            ret.Sitemaps.AddRange(sitemaps.Distinct(StringComparer.Ordinal));

            foreach (var g in selected)
            {
                if (g.CrawlDelaySeconds.HasValue)
                    ret.CrawlDelaySeconds = ret.CrawlDelaySeconds.HasValue
                        ? Math.Max(ret.CrawlDelaySeconds.Value, g.CrawlDelaySeconds.Value)
                        : g.CrawlDelaySeconds.Value;
                ret.Rules.AddRange(g.Rules);
            }

            return ret;
        }

        private static List<RobotGroup> SelectBestGroups(List<RobotGroup> groups, string userAgent)
        {
            var ua = (userAgent ?? string.Empty).Trim();
            var matches = new List<(RobotGroup Group, int Specificity)>();

            foreach (var g in groups)
            {
                foreach (var agent in g.Agents)
                {
                    var spec = AgentSpecificity(agent, ua);
                    if (spec >= 0)
                    {
                        matches.Add((g, spec));
                        break;
                    }
                }
            }

            if (matches.Count == 0)
                return groups.Where(g => g.Agents.Any(a => a == "*")).ToList();

            var best = matches.Max(x => x.Specificity);
            return matches.Where(x => x.Specificity == best).Select(x => x.Group).Distinct().ToList();
        }

        private static int AgentSpecificity(string agent, string userAgent)
        {
            if (string.IsNullOrWhiteSpace(agent)) return -1;
            if (agent == "*") return 0;

            var a = agent.Trim();
            var ua = userAgent ?? string.Empty;
            return ua.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0 ? a.Length : -1;
        }

        private static RobotRule? FindBestRuleMatch(IEnumerable<RobotRule> rules, string pathAndQuery)
        {
            RobotRule? bestRule = null;
            int bestLength = -1;

            foreach (var rule in rules)
            {
                if (string.IsNullOrEmpty(rule.Pattern)) continue;
                if (!PatternMatches(pathAndQuery, rule.Pattern, out var matchLen)) continue;

                if (matchLen > bestLength)
                {
                    bestLength = matchLen;
                    bestRule = rule;
                }
                else if (matchLen == bestLength && bestRule != null && rule.Allow && !bestRule.Allow)
                {
                    // Allow wins tie
                    bestRule = rule;
                }
            }

            return bestRule;
        }

        private static bool PatternMatches(string pathAndQuery, string pattern, out int matchLength)
        {
            matchLength = 0;
            if (pattern == "/")
            {
                matchLength = 1;
                return true;
            }

            if (pattern.Contains('*') || pattern.EndsWith("$", StringComparison.Ordinal))
            {
                var regex = "^" + Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\$", "$");
                var ok = Regex.IsMatch(pathAndQuery, regex, RegexOptions.None, TimeSpan.FromSeconds(1));
                if (ok)
                {
                    var p = pattern.EndsWith("$", StringComparison.Ordinal) ? pattern[..^1] : pattern;
                    matchLength = p.Replace("*", string.Empty).Length;
                }
                return ok;
            }

            var starts = pathAndQuery.StartsWith(pattern, StringComparison.Ordinal);
            if (starts) matchLength = pattern.Length;
            return starts;
        }

        private static string GetOrigin(Uri uri)
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
            foreach (var kv in _hostLocks)
            {
                try { kv.Value.Dispose(); } catch { }
            }
        }
    }
}
