using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public class NopeChaCaptchaSolver : ICaptchaSolver
    {
        private readonly HttpClient _http;
        private readonly IConfiguration _config;
        private readonly ILogger<NopeChaCaptchaSolver> _logger;

        public NopeChaCaptchaSolver(HttpClient http, IConfiguration config, ILogger<NopeChaCaptchaSolver> logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<CaptchaSolutionResult> SolveAsync(FetchResult challenge, CancellationToken ct)
        {
            try
            {
                if (!_config.GetValue<bool>("Crawler:EnableNopeCha", false))
                    return new CaptchaSolutionResult(false, Error: "NopeCHA disabled");

                var apiKey = _config["Crawler:NopeChaApiKey"];
                if (string.IsNullOrWhiteSpace(apiKey))
                    return new CaptchaSolutionResult(false, Error: "NopeCHA API key missing");

                var endpoint = _config.GetValue<string>("Crawler:NopeChaEndpoint", "https://api.nopecha.com/solve");

                var payload = new
                {
                    url = string.IsNullOrWhiteSpace(challenge.FinalUrl) ? challenge.Url : challenge.FinalUrl,
                    html = challenge.RawHtml,
                    snippet = challenge.ChallengeSnippet
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(payload)
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("NopeCHA adapter returned {Status} for {Url}", (int)resp.StatusCode, challenge.Url);
                    return new CaptchaSolutionResult(false, Error: $"Adapter returned {(int)resp.StatusCode}");
                }

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("success", out var succ) && succ.GetBoolean())
                    {
                        var sol = doc.RootElement.TryGetProperty("solution", out var s) ? s.ToString() : null;
                        Dictionary<string, string>? cookies = null;
                        if (doc.RootElement.TryGetProperty("cookies", out var cEl) && cEl.ValueKind == JsonValueKind.Object)
                        {
                            cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var p in cEl.EnumerateObject())
                                cookies[p.Name] = p.Value.GetString() ?? string.Empty;
                        }

                        _logger.LogInformation("NopeCHA adapter reported success for {Url}", challenge.Url);
                        return new CaptchaSolutionResult(true, Solution: sol, Cookies: cookies, Error: null);
                    }

                    var err = doc.RootElement.TryGetProperty("error", out var e) ? e.ToString() : "unknown";
                    return new CaptchaSolutionResult(false, Error: err);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse NopeCHA response for {Url}", challenge.Url);
                    return new CaptchaSolutionResult(false, Error: "invalid adapter response");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NopeCHA adapter failed for {Url}", challenge.Url);
                return new CaptchaSolutionResult(false, Error: ex.Message);
            }
        }
    }
}
