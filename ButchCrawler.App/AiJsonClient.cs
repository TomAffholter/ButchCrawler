using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ButchCrawler.App;

public sealed class AiJsonClient(HttpClient http, IConfiguration config, IAiRepository repository)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<string> GenerateAsync(string model, string instructions, object[] content, string schemaName, object schema, CancellationToken ct)
    {
        if (!config.GetValue<bool>("AI:Enabled", false)) throw new AiResearchException("AI features are disabled in Settings.");
        var key = config["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) throw new AiResearchException("Add your OpenAI key using Settings → OpenAI key.");
        var attempts = Math.Clamp(config.GetValue<int>("AI:MaxAttempts", 2), 1, 3);
        var body = JsonSerializer.Serialize(new
        {
            model, store = false, instructions,
            input = new[] { new { role = "user", content } },
            max_output_tokens = Math.Clamp(config.GetValue<int>("AI:MaxOutputTokens", 3000), 1000, 12000),
            text = new { format = new { type = "json_schema", name = schemaName, strict = true, schema } }
        });
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (!await repository.ConsumeAiRequestAsync(Math.Clamp(config.GetValue<int>("AI:DailyRequestLimit", 50), 1, 10000), ct).ConfigureAwait(false))
                    throw new AiResearchException("The AI daily request limit has been reached. Saved results remain available; the limit resets at midnight UTC.");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue<int>("AI:RequestTimeoutSeconds", 120), 1, 1800)));
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
                { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                try
                {
                    using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        var billing = OpenAiBilling.BillingError(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                        if (billing != null) { OpenAiBilling.Report(billing); throw new AiResearchException(billing); }
                    }
                    var transient = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
                    if (transient && attempt < attempts)
                    {
                        var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? Math.Pow(2, attempt);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 60)), ct).ConfigureAwait(false); continue;
                    }
                    if (!response.IsSuccessStatusCode) throw new AiResearchException($"AI returned HTTP {(int)response.StatusCode}. Check your key, model access and quota.", transient);
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                    var root = document.RootElement;
                    if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
                        throw new AiResearchException("AI did not finish its response. Increase the output limit or reduce the source size.", true);
                    var result = new StringBuilder();
                    foreach (var output in root.GetProperty("output").EnumerateArray())
                    {
                        if (!output.TryGetProperty("content", out var parts)) continue;
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.GetProperty("type").GetString() == "refusal") throw new AiResearchException("The AI declined this request.");
                            if (part.GetProperty("type").GetString() == "output_text") result.Append(part.GetProperty("text").GetString());
                        }
                    }
                    if (result.Length == 0) throw new AiResearchException("AI returned no usable result.");
                    using var validation = JsonDocument.Parse(result.ToString());
                    OpenAiBilling.RequestSucceeded();
                    return result.ToString();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { if (attempt >= attempts) throw new AiResearchException("AI request timed out.", true); }
                catch (HttpRequestException) { if (attempt >= attempts) throw new AiResearchException("Could not connect to the AI service.", true); }
                catch (JsonException) { throw new AiResearchException("AI returned an invalid structured response."); }
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }
}
