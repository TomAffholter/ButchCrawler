using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    public class CaptchaOperator
    {
        private readonly SqlStorage _storage;
        private readonly Fetcher _fetcher;

        public CaptchaOperator(SqlStorage storage, Fetcher fetcher)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        }

        public async Task RunCaptchaSolveLoopAsync(CancellationToken ct = default)
        {
            Console.WriteLine("Starting CAPTCHA operator loop. Press 'q' to quit.");

            while (!ct.IsCancellationRequested)
            {
                var tasks = await _storage.GetPendingCaptchaTasksAsync(limit: 20);
                if (tasks == null || tasks.Count == 0)
                {
                    Console.WriteLine("No pending CAPTCHAs.");
                    return;
                }

                foreach (var t in tasks)
                {
                    if (ct.IsCancellationRequested) return;

                    Console.WriteLine($"Task {t.Id}: {t.Url}");
                    if (!string.IsNullOrEmpty(t.ScreenshotPath) && File.Exists(t.ScreenshotPath) &&
                        new[] { ".png", ".jpg", ".jpeg", ".bmp" }.Contains(Path.GetExtension(t.ScreenshotPath), StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var p = new Process();
                            p.StartInfo.FileName = t.ScreenshotPath;
                            p.StartInfo.UseShellExecute = true;
                            p.Start();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to open screenshot: {ex.Message}");
                        }
                    }

                    Console.WriteLine("HTML snippet:");
                    if (!string.IsNullOrEmpty(t.HtmlSnippet))
                    {
                        var snippet = t.HtmlSnippet.Length > 2000 ? t.HtmlSnippet.Substring(0, 2000) : t.HtmlSnippet;
                        Console.WriteLine(snippet);
                    }
                    else
                    {
                        Console.WriteLine("(no snippet)");
                    }

                    while (true)
                    {
                        Console.WriteLine("Action: (s)olve / (k)skip / (i)gnore / (o)pen / (q)uit");
                        var cmd = Console.ReadLine()?.Trim().ToLowerInvariant();
                        if (string.IsNullOrEmpty(cmd)) continue;
                        if (cmd == "q") return;
                        if (cmd == "k") break; // skip to next task
                        if (cmd == "i")
                        {
                            await _storage.MarkCaptchaIgnoredAsync(t.Id);
                            Console.WriteLine("Marked ignored.");
                            break;
                        }
                        if (cmd == "o")
                        {
                            // open the URL in default browser for manual solving
                            try
                            {
                                if (!Uri.TryCreate(t.Url, UriKind.Absolute, out var safeUri) || safeUri.Scheme is not ("http" or "https"))
                                    throw new InvalidOperationException("Only HTTP and HTTPS challenge links can be opened.");
                                var p = new Process();
                                p.StartInfo.FileName = safeUri.AbsoluteUri;
                                p.StartInfo.UseShellExecute = true;
                                p.Start();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Failed to open browser: {ex.Message}");
                            }
                            continue;
                        }

                        if (cmd == "s")
                        {
                            Console.WriteLine("Solution type (cookies|token|other):");
                            var solType = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "other";
                            Console.WriteLine("Paste solution value and press ENTER:");
                            var val = Console.ReadLine()?.Trim() ?? string.Empty;
                            if (string.IsNullOrEmpty(val))
                            {
                                Console.WriteLine("No solution provided, returning to action prompt.");
                                continue;
                            }

                            // optional reuse
                            Console.WriteLine("Reuse for minutes (0 = one-off):");
                            var reuseLine = Console.ReadLine();
                            int reuseMins = 0;
                            int.TryParse(reuseLine, out reuseMins);

                            var solObj = new Dictionary<string, string>
                            {
                                ["type"] = solType,
                                ["value"] = val
                            };
                            var solJson = JsonSerializer.Serialize(solObj);

                            await _storage.SaveCaptchaSolutionAsync(t.Id, solJson, reuseMins > 0 ? TimeSpan.FromMinutes(reuseMins) : (TimeSpan?)null);
                            Console.WriteLine("Solution saved. Performing one-off fetch of the URL using provided solution...");

                            // prepare headers
                            var headers = new Dictionary<string,string>();
                            if (solType == "cookies")
                            {
                                headers["Cookie"] = val;
                            }
                            else if (solType == "token")
                            {
                                headers["Authorization"] = val;
                            }
                            else
                            {
                                headers["X-Captcha-Solution"] = val;
                            }

                            var fr = await _fetcher.FetchWithHeadersAsync(t.Url, headers, ct);
                            if (fr != null && fr.Success)
                            {
                                Console.WriteLine("One-off fetch succeeded. Storing result and marking task solved.");
                                // store result text
                                await _storage.StoreCrawlResultAsync(t.Url, fr.StatusCode ?? 0, fr.Text ?? string.Empty, DateTime.UtcNow, fr.UserAgent, fr.RemoteIp, fr.Asn, "manual");
                                await _storage.UpsertVisitedUrlAsync(t.Url, fr.ContentLength.HasValue ? null : null, fr.StatusCode ?? 0, DateTime.UtcNow, fr.UserAgent, fr.RemoteIp, fr.Asn, "manual");
                            }
                            else
                            {
                                Console.WriteLine($"One-off fetch failed: {fr?.Error}");
                            }

                            break; // move to next task
                        }

                        Console.WriteLine("Unknown action");
                    }
                }
            }
        }
    }
}
