using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO;
using Polly;
using Polly.Retry;

namespace ButchCrawler.App
{
    public class EmbeddingService
    {
        private readonly HttpClient _http;
        private readonly ILogger<EmbeddingService> _logger;
        private readonly string _apiKey;
        private readonly IMetricsService _metrics;

        public EmbeddingService(HttpClient http, ILogger<EmbeddingService> logger, IConfiguration config, IMetricsService metrics)
        {
            _http = http;
            _logger = logger;
            _apiKey = config["OpenAI:ApiKey"] ?? throw new Exception("Missing OpenAI API key");
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }

        public async Task<float[]> GenerateEmbeddingAsync(string text)
        {
            var request = new
            {
                model = "text-embedding-3-large",
                input = text
            };

            // A new HttpRequestMessage is required on every retry.
            var retryPolicy = Policy<HttpResponseMessage>
                .Handle<HttpRequestException>()
                .Or<IOException>()
                .OrResult(r => (int)r.StatusCode == 429 || (int)r.StatusCode >= 500)
                .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                    onRetry: (outcome, delay, attempt, _) =>
                    {
                        if (outcome.Exception != null)
                            _logger.LogWarning("Embedding transport failure; retry {Attempt} after {Delay}: {Reason}", attempt, delay, outcome.Exception.Message);
                        else
                        {
                            _logger.LogWarning("Embedding HTTP {Status}; retry {Attempt} after {Delay}", (int)outcome.Result.StatusCode, attempt, delay);
                            outcome.Result.Dispose();
                        }
                    });

            using var response = await retryPolicy.ExecuteAsync(async () =>
            {
                using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                msg.Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
                var apiResponse = await _http.SendAsync(msg).ConfigureAwait(false);
                if (!apiResponse.IsSuccessStatusCode)
                {
                    var billing = OpenAiBilling.BillingError(await apiResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
                    if (billing != null)
                    {
                        apiResponse.Dispose(); OpenAiBilling.Report(billing);
                        throw new AiResearchException(billing);
                    }
                }
                return apiResponse;
            });
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                // Log detailed error to help diagnose 4xx/5xx responses from the API
                _logger.LogError("Embedding API returned {Status} for text length {Len}: {Body}", (int)response.StatusCode, text?.Length ?? 0, body);
                try { _metrics.IncrementEmbeddingFailure(); } catch { }
                response.EnsureSuccessStatusCode();
            }

            using var doc = JsonDocument.Parse(body);
            OpenAiBilling.RequestSucceeded();

            return doc.RootElement
                .GetProperty("data")[0]
                .GetProperty("embedding")
                .EnumerateArray()
                .Select(x => x.GetSingle())
                .ToArray();
        }
    }
}
