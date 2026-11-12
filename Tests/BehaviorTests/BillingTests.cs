using System.Net;
using System.Net.Http;
using System.Text.Json;
using ButchCrawler.App;
using ButchCrawler.Operator;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class BillingTests
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => Task.FromResult(send(request)); }
    private static string Error(string code) => JsonSerializer.Serialize(new { error=new { code,type="insufficient_quota",message="secret raw server text" } });
    private sealed class Repo : IAiRepository
    {
        public Task<bool> ConsumeAiRequestAsync(int limit,CancellationToken ct) => Task.FromResult(true);
        public Task<IAsyncDisposable> AcquireAiWorkAsync(string key,CancellationToken ct) => throw new NotSupportedException();
        public Task<AiSavedResult?> GetAiResultAsync(string task,string source,string hash,string model,CancellationToken ct) => Task.FromResult<AiSavedResult?>(null);
        public Task<AiSavedResult> SaveAiResultAsync(string task,string source,string hash,string model,string text,string json,CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class AudioStore : IAudioTranscriptStore
    {
        public Task<AudioTranscript?> GetAudioTranscriptAsync(string source,string hash,string model,CancellationToken ct) => Task.FromResult<AudioTranscript?>(null);
        public Task SaveAudioTranscriptAsync(AudioTranscript transcript,CancellationToken ct) => throw new Exception("A billing failure must not save a transcript");
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        try
        {
            foreach(var code in new[]{"credit_balance_exhausted","insufficient_quota","organization_spend_limit_exceeded","project_spend_limit_exceeded","organization_usage_limit_exceeded"})
                check(OpenAiBilling.BillingError(Error(code)) is string message && message.Contains(OpenAiBilling.BillingUrl) && !message.Contains("secret"),"billing code gets actionable account link without raw server text: "+code);
            check(OpenAiBilling.BillingError("{\"error\":{\"code\":\"rate_limit_exceeded\",\"type\":\"requests\"}}") == null,"temporary rate limit is not mislabeled as exhausted credit");
            check(OpenAiBilling.BillingError("not JSON") == null && OpenAiBilling.BillingError("{\"error\":\"oops\"}")==null,"malformed API error does not break billing classifier");
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["OpenAI:ApiKey"]="fake-test-key",["AI:Enabled"]="true",["AI:MaxAttempts"]="3",["Audio:Enabled"]="true",["Audio:MaxAttempts"]="3" }).Build();
            var calls=0;
            using var http=new HttpClient(new Handler(_=> { calls++; return new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent(Error("credit_balance_exhausted"))}; }));
            try { await new AiJsonClient(http,config,new Repo()).GenerateAsync("model","test",Array.Empty<object>(),"test",AiResearchService.ResultSchema(),default); throw new Exception("Expected billing error"); }
            catch(AiResearchException ex) { check(!ex.Retryable && calls==1 && ex.Message.Contains(OpenAiBilling.BillingUrl),"AI exhausted-credit response stops retries and supplies billing link"); }
            try { await new AudioProcessor(http,config,new AudioStore()).ProcessAsync("test.wav",new byte[]{1},"audio/wav",".wav",default); throw new Exception("Expected audio billing error"); }
            catch(AudioTranscriptionException ex) { check(!ex.Retryable && calls==2,"audio exhausted-credit response stops retries"); }
            try { await new EmbeddingService(http,NullLogger<EmbeddingService>.Instance,config,new MetricsService()).GenerateEmbeddingAsync("test"); throw new Exception("Expected embedding billing error"); }
            catch(AiResearchException) { check(calls==3,"embedding exhausted-credit response stops retry policy"); }
            check(OpenAiBilling.Status.Contains("credits have run out"),"About status reflects latest observed API billing failure");
            check(ActivityLog.Classify(OpenAiBilling.LogPrefix+"warning").Category=="AI account notice","billing notice receives visible warning styling");
        }
        finally { OpenAiBilling.ClearNotice(); }
    }
}
