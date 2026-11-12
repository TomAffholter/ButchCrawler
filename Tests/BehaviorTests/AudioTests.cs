using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using ButchCrawler.App;
using Microsoft.Extensions.Configuration;

internal static class AudioTests
{
    private static IConfiguration Settings(params (string Key, string? Value)[] values)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Audio:Enabled"] = "true", ["OpenAI:ApiKey"] = "fake-test-key", ["Audio:Model"] = "gpt-transcribe",
            ["Audio:MaxAttempts"] = "1", ["Audio:FFmpegPath"] = @"C:\ffmpeg\bin\ffmpeg.exe"
        };
        foreach (var (key, value) in values) settings[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
    private static HttpResponseMessage Transcript(string text = "Historical records were discussed.") => new(HttpStatusCode.OK)
    { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { text })) };

    public static async Task RunAsync(Action<bool, string> check)
    {
        const string source = "https://example.test/Interview.wav";
        var bytes = new byte[] { 1, 2, 3, 4 };
        var temporaryRoot = Path.Combine(Environment.CurrentDirectory, "Tests", "work", "audio-cleanup-" + Guid.NewGuid().ToString("N"));
        var store = new MemoryStore(); var calls = 0;
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            calls++;
            check(request.RequestUri!.ToString() == "https://api.openai.com/v1/audio/transcriptions" && request.Headers.Authorization?.Parameter == "fake-test-key", "audio uses fixed transcription endpoint and configured key");
            var parts = ((MultipartFormDataContent)request.Content!).ToArray();
            check(parts.Length == 2 && await parts[0].ReadAsStringAsync(ct) == "gpt-transcribe" && (await parts[1].ReadAsByteArrayAsync(ct)).SequenceEqual(bytes), "audio submits model and original file as multipart content");
            check(!Directory.Exists(temporaryRoot), "direct audio transcription uploads from memory without creating a physical file");
            return Transcript();
        }));
        var processor = new AudioProcessor(client, Settings(), store, temporaryRoot);
        var result = await processor.ProcessAsync(source, bytes, "audio/wav", ".wav", default);
        check(result.Text == "Historical records were discussed." && store.Rows.Count == 1, "audio saves full transcript independently of embedding indexing");
        var withoutKey = new AudioProcessor(client, Settings(("OpenAI:ApiKey", null)), store);
        check((await withoutKey.ProcessAsync(source, bytes, "audio/wav", ".wav", default)).Text == result.Text && calls == 1, "unchanged audio reuses cached transcript without key or API call");
        var registry = new PayloadProcessorRegistry(); processor.Register(registry);
        check(ReferenceEquals(registry.Resolve(null, ".MP3"), processor) && ReferenceEquals(registry.Resolve("audio/unknown", null), processor), "registry resolves audio extensions and audio MIME fallback");
        var links = new HtmlParser().Parse("https://example.test/page", "<audio src='/Talk.mp3'></audio><audio><source src='/Voice.wav'></audio><a href='/file.m4a'>Recording</a>").Links;
        check(links.Contains("https://example.test/Talk.mp3") && links.Contains("https://example.test/Voice.wav") && links.Contains("https://example.test/file.m4a"), "crawler discovers embedded audio and download links");

        using var never = new HttpClient(new Handler((_, _) => throw new Exception("Unexpected API call")));
        async Task Reject(IConfiguration settings, byte[] data, string label)
        {
            try { await new AudioProcessor(never, settings, new MemoryStore()).ProcessAsync(source, data, "audio/wav", ".wav", default); throw new Exception("Expected rejection: " + label); }
            catch (AudioTranscriptionException) { check(true, label); }
        }
        await Reject(Settings(("Audio:Enabled", "false")), bytes, "disabled automatic audio does not call API");
        await Reject(Settings(), Array.Empty<byte>(), "empty audio rejected before upload");
        await Reject(Settings(("Audio:MaxSourceBytes", "2")), bytes, "source size limit rejects oversized audio before upload");
        await Reject(Settings(("OpenAI:ApiKey", null)), bytes, "missing audio API key produces actionable error");

        var permanentCalls = 0;
        using var forbidden = new HttpClient(new Handler((_, _) =>
        { permanentCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }));
        try { await new AudioProcessor(forbidden, Settings(("Audio:MaxAttempts", "3")), new MemoryStore()).ProcessAsync(source, bytes, null, ".wav", default); throw new Exception("Expected authentication failure"); }
        catch (AudioTranscriptionException ex) { check(!ex.Retryable && permanentCalls == 1 && !ex.Message.Contains("fake-test-key"), "audio authentication failures do not retry or expose key"); }

        var retryCalls = 0;
        using var retryClient = new HttpClient(new Handler((_, _) =>
        {
            retryCalls++;
            if (retryCalls > 1) return Task.FromResult(Transcript("Retry succeeded"));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return Task.FromResult(response);
        }));
        var retried = await new AudioProcessor(retryClient, Settings(("Audio:MaxAttempts", "2")), new MemoryStore()).ProcessAsync(source, bytes, null, ".wav", default);
        check(retryCalls == 2 && retried.Text == "Retry succeeded", "audio rate limits retry with fresh multipart requests");

        using var badJson = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("invalid-json") })));
        try { await new AudioProcessor(badJson, Settings(), new MemoryStore()).ProcessAsync(source, bytes, null, ".wav", default); throw new Exception("Expected invalid response failure"); }
        catch (AudioTranscriptionException) { check(true, "invalid audio JSON cannot become a successful transcript"); }

        using var stalled = new HttpClient(new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Transcript(); }));
        try { await new AudioProcessor(stalled, Settings(("Audio:RequestTimeoutSeconds", "1")), new MemoryStore()).ProcessAsync(source, bytes, null, ".wav", default); throw new Exception("Expected timeout"); }
        catch (AudioTranscriptionException ex) { check(ex.Retryable, "audio requests obey timeout and report retryable failure"); }
        using (var cancel = new CancellationTokenSource(50))
        {
            try { await new AudioProcessor(stalled, Settings(), new MemoryStore()).ProcessAsync(source, bytes, null, ".wav", cancel.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { check(true, "audio caller cancellation propagates"); }
        }

        var cappedCalls = 0;
        using var capped = new HttpClient(new Handler((_, _) =>
        { cappedCalls++; var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero); return Task.FromResult(response); }));
        try { await new AudioProcessor(capped, Settings(("Audio:MaxApiRequestsPerRun", "1"), ("Audio:MaxAttempts", "3")), new MemoryStore()).ProcessAsync(source, bytes, null, ".wav", default); throw new Exception("Expected request limit"); }
        catch (AudioTranscriptionException) { check(cappedCalls == 1, "audio request budget caps retries as well as segments"); }

        // Exercise real local conversion/splitting; uploads still go only to an in-memory handler.
        try
        {
        var wav = Wave(3); var chunkStore = new MemoryStore(); var chunkCalls = 0;
        using var chunkClient = new HttpClient(new Handler((_, _) => Task.FromResult(Transcript("Segment " + ++chunkCalls))));
        var splitSettings = Settings(("Audio:MaxUploadBytes", "20000"), ("Audio:ChunkSeconds", "1"), ("Audio:MaxChunks", "10"));
        var split = await new AudioProcessor(chunkClient, splitSettings, chunkStore, temporaryRoot).ProcessAsync(source, wav, null, ".wav", default);
        check(chunkCalls >= 3 && split.Text.Contains("Segment 1") && split.Text.Contains("Segment " + chunkCalls), "large audio is split by FFmpeg and transcript segments are combined");
        check(!Directory.EnumerateFileSystemEntries(temporaryRoot).Any(), "successful transcription deletes temporary source audio and converted segments");
        check(chunkStore.Rows.Values.Single().Segments.Select((segment, i) => segment.StartSeconds == i).All(value => value), "audio transcript preserves segment offsets");
        var resumeStore = new MemoryStore(); var interruptedCalls = 0;
        using var interrupted = new HttpClient(new Handler((_, _) => Task.FromResult(++interruptedCalls == 1
            ? Transcript("Preserved first segment") : new HttpResponseMessage(HttpStatusCode.BadRequest))));
        try { await new AudioProcessor(interrupted, splitSettings, resumeStore, temporaryRoot).ProcessAsync(source, wav, null, ".wav", default); throw new Exception("Expected interrupted recording"); }
        catch (AudioTranscriptionException) { check(resumeStore.Rows.Values.Single().Segments.Count == 1 && !resumeStore.Rows.Values.Single().IsComplete, "audio preserves partial progress when a later segment fails"); }
        check(!Directory.EnumerateFileSystemEntries(temporaryRoot).Any(), "failed transcription deletes temporary audio while keeping saved text progress");
        var resumedCalls = 0;
        using var resumedClient = new HttpClient(new Handler((_, _) => Task.FromResult(Transcript("Resumed segment " + ++resumedCalls))));
        var resumed = await new AudioProcessor(resumedClient, splitSettings, resumeStore, temporaryRoot).ProcessAsync(source, wav, null, ".wav", default);
        check(resumedCalls == chunkCalls - 1 && resumed.Text.StartsWith("Preserved first segment") && resumeStore.Rows.Values.Single().IsComplete,
            "audio resumes saved segments without uploading the first segment again");
        try { await new AudioProcessor(never, Settings(("Audio:MaxUploadBytes", "20000"), ("Audio:ChunkSeconds", "1"), ("Audio:MaxChunks", "1")), new MemoryStore(), temporaryRoot).ProcessAsync(source, wav, null, ".wav", default); throw new Exception("Expected duration rejection"); }
        catch (AudioTranscriptionException ex) { check(ex.Message.Contains("MaxChunks"), "overlong audio is rejected without uploading a truncated recording"); }
        using var cancelSplit = new CancellationTokenSource();
        using var canceledSplitClient = new HttpClient(new Handler(async (_, ct) => { cancelSplit.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Transcript(); }));
        try { await new AudioProcessor(canceledSplitClient, splitSettings, new MemoryStore(), temporaryRoot).ProcessAsync(source, wav, null, ".wav", cancelSplit.Token); throw new Exception("Expected split cancellation"); }
        catch (OperationCanceledException) { check(!Directory.EnumerateFileSystemEntries(temporaryRoot).Any(), "canceling a large transcription also deletes all temporary audio"); }
        }
        catch (AudioTranscriptionException ex) when (ex.Message.Contains("FFmpeg was not found", StringComparison.OrdinalIgnoreCase))
        {
            check(true, "FFmpeg conversion checks are skipped when FFmpeg is not installed");
        }
        finally { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true); }
        check(ActivityLogCategory(), "audio progress and completion have distinct activity colors");
        var renewals = 0;
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guarded = await AudioQueueLeaseGuard.RunAsync(async ct => { await renewed.Task.WaitAsync(ct); return "saved"; },
            () => { Interlocked.Increment(ref renewals); renewed.TrySetResult(); return Task.FromResult(true); }, default, TimeSpan.FromMilliseconds(10));
        check(guarded == "saved" && renewals > 0, "long audio work renews its queue lease until finished");
        try
        {
            await AudioQueueLeaseGuard.RunAsync(async ct => { await Task.Delay(Timeout.Infinite, ct); return "unexpected"; },
                () => Task.FromResult(false), default, TimeSpan.FromMilliseconds(10));
            throw new Exception("Expected lease loss");
        }
        catch (AudioTranscriptionException ex) { check(ex.Retryable, "lost queue ownership cancels audio before duplicate work continues"); }
    }

    private static bool ActivityLogCategory() => ButchCrawler.Operator.ActivityLog.Classify("[CRAWL-URL] AUDIO | segment 1").Category == "Transcribing audio" && ButchCrawler.Operator.ActivityLog.Classify("[CRAWL-URL] AUDIO-COMPLETE | saved").Bold;
    private static byte[] Wave(int seconds)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var samples = 16000 * seconds; var size = samples * 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(size + 36); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(size);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 16000) * 3000));
        return stream.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken); }
    private sealed class MemoryStore : IAudioTranscriptStore
    {
        public Dictionary<string, AudioTranscript> Rows { get; } = new(StringComparer.Ordinal);
        public Task<AudioTranscript?> GetAudioTranscriptAsync(string source, string hash, string model, CancellationToken ct)
            => Task.FromResult(Rows.GetValueOrDefault(source + "|" + hash + "|" + model));
        public Task SaveAudioTranscriptAsync(AudioTranscript transcript, CancellationToken ct)
        { Rows[transcript.Source + "|" + transcript.ContentHash + "|" + transcript.Model] = transcript; return Task.CompletedTask; }
    }
}
