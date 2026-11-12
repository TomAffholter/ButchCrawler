using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public static class CrawlerLoop
    {
        // Loop through URLs, record external errors and continue; surface internal errors.
        public static async Task ProcessUrlsAsync(IEnumerable<string> urls, Fetcher fetcher, ILogger logger, CancellationToken ct)
        {
            var errorsPath = Path.Combine(AppContext.BaseDirectory, "fetch_errors.jsonl");
            await using var sw = new StreamWriter(new FileStream(errorsPath, FileMode.Append, FileAccess.Write, FileShare.Read));

            foreach (var url in urls)
            {
                if (ct.IsCancellationRequested) break;

                FetchResult fr;
                try
                {
                    fr = await fetcher.FetchAsync(url, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogInformation("Cancellation requested, stopping crawl.");
                    break;
                }
                catch (Exception ex)
                {
                    // Unexpected exception from Fetcher -> treat as internal and surface
                    logger.LogError(ex, "Internal error fetching {Url}", url);
                    throw;
                }

                if (fr.Success)
                {
                    logger.LogInformation("Fetched {Url} — {Bytes} bytes", url, fr.BodyBytes?.Length ?? fr.RawHtml?.Length ?? 0);
                    // process fr -> index/store etc.
                    continue;
                }

                if (IsExternalError(fr))
                {
                    logger.LogWarning("External error for {Url}: {Error} (status:{StatusCode})", url, fr.Error, fr.StatusCode);

                    var record = new
                    {
                        Url = fr.Url,
                        FinalUrl = fr.FinalUrl,
                        Error = fr.Error,
                        StatusCode = fr.StatusCode,
                        IsChallenge = fr.IsChallenge,
                        ChallengeType = fr.ChallengeType,
                        TimestampUtc = DateTime.UtcNow
                    };

                    var line = JsonSerializer.Serialize(record);
                    await sw.WriteLineAsync(line).ConfigureAwait(false);
                    await sw.FlushAsync().ConfigureAwait(false);

                    // resume to next URL
                    continue;
                }

                // Non-success and not classified as external -> treat as internal/fixable
                logger.LogError("Fetch failure for {Url}: {Error} (status:{StatusCode})", url, fr.Error, fr.StatusCode);
                throw new InvalidOperationException($"Internal fetch failure for {url}: {fr.Error}");
            }
        }

        // Classify failures that are 'not our fault' (network, remote server, rate-limit, challenges)
        private static bool IsExternalError(FetchResult fr)
        {
            if (fr == null) return false;

            if (fr.IsChallenge) return true;

            if (!string.IsNullOrEmpty(fr.Error))
            {
                var e = fr.Error.ToLowerInvariant();
                if (e.Contains("timeout") || e.Contains("connect") || e.Contains("connection could not be established") ||
                    e.Contains("429") || e.Contains("too many requests") || e.Contains("forbidden") ||
                    e.Contains("challenge") || e.Contains("captcha"))
                    return true;
            }

            if (fr.StatusCode.HasValue)
            {
                var s = fr.StatusCode.Value;
                if (s == 429 || s == 403 || (s >= 500 && s < 600)) return true;
            }

            return false;
        }
    }
}