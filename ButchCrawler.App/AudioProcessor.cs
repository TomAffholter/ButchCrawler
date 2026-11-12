using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ButchCrawler.App;

public sealed class AudioProcessor : IPayloadProcessor
{
    public static readonly string[] Extensions = { ".mp3", ".mp4", ".mpeg", ".mpga", ".m4a", ".wav", ".webm", ".flac", ".ogg", ".aac", ".wma", ".aiff" };
    private static readonly HashSet<string> DirectFormats = new(new[] { ".mp3", ".mp4", ".mpeg", ".mpga", ".m4a", ".wav", ".webm" }, StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly IAudioTranscriptStore _store;
    private readonly string _temporaryRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _apiRequests;

    public AudioProcessor(HttpClient http, IConfiguration config, IAudioTranscriptStore store, string? temporaryRoot = null)
    { _http = http; _config = config; _store = store; _temporaryRoot = temporaryRoot ?? Path.Combine(Path.GetTempPath(), "ButchCrawlerAudio"); }

    public static bool IsAudio(string? contentType, string? extension) =>
        contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ||
        (!string.IsNullOrEmpty(extension) && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) ||
        string.Equals(contentType, "video/mp4", StringComparison.OrdinalIgnoreCase) || string.Equals(contentType, "video/webm", StringComparison.OrdinalIgnoreCase);

    public void Register(PayloadProcessorRegistry registry)
    {
        foreach (var extension in Extensions) registry.RegisterExtension(extension, this);
        foreach (var mime in new[] { "audio/mpeg", "audio/mp3", "audio/mp4", "audio/x-m4a", "audio/wav", "audio/x-wav", "audio/wave", "audio/webm", "audio/ogg", "audio/flac", "audio/x-flac", "audio/aac", "audio/x-ms-wma", "audio/aiff", "video/mp4", "video/webm" })
            registry.RegisterMime(mime, this);
    }

    public async Task<ProcessedPayload> ProcessAsync(string url, byte[] body, string? contentType, string? extension, CancellationToken ct)
    {
        if (!_config.GetValue<bool>("Audio:Enabled", false)) throw new AudioTranscriptionException("Audio transcription is disabled. Enable Audio : Enabled in Settings first.");
        if (body.Length == 0) throw new AudioTranscriptionException("The audio file is empty.");
        if (body.LongLength > _config.GetValue<long>("Audio:MaxSourceBytes", 500_000_000)) throw new AudioTranscriptionException("Audio exceeds the configured source-file size limit.");
        if (url.Length > 2000) throw new AudioTranscriptionException("The audio source path is too long to store.");
        var hash = SHA256.HashData(body); var hashHex = Convert.ToHexString(hash);
        var model = _config["Audio:Model"] ?? "gpt-transcribe";
        if (string.IsNullOrWhiteSpace(model) || model.Length > 128) throw new AudioTranscriptionException("Invalid Audio : Model setting.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        string? directory = null;
        try
        {
            var cached = await _store.GetAudioTranscriptAsync(url, hashHex, model, ct).ConfigureAwait(false);
            if (cached?.IsComplete == true)
            {
                Console.WriteLine($"[CRAWL-URL] AUDIO-CACHED | Saved transcript reused | {url}");
                return Payload(cached.Text, hash, url);
            }
            var key = _config["OpenAI:ApiKey"];
            if (string.IsNullOrWhiteSpace(key)) throw new AudioTranscriptionException("No OpenAI key is configured. Add one using OpenAI key, then restart the crawler.");
            var uploadLimit = Math.Clamp(_config.GetValue<int>("Audio:MaxUploadBytes", 24_000_000), 1000, 24_000_000);
            var chunkSeconds = Math.Clamp(_config.GetValue<int>("Audio:ChunkSeconds", 600), 1, 1200);
            var maxChunks = Math.Clamp(_config.GetValue<int>("Audio:MaxChunks", 60), 1, 120);
            extension = NormalizeExtension(extension, contentType);
            string[] files;
            var split = body.Length > uploadLimit || !DirectFormats.Contains(extension);
            if (split)
            {
                directory = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var input = Path.Combine(directory, "input" + extension);
                await File.WriteAllBytesAsync(input, body, ct).ConfigureAwait(false);
                await ConvertAsync(input, directory, chunkSeconds, maxChunks, ct).ConfigureAwait(false);
                files = Directory.GetFiles(directory, "part-*.mp3").OrderBy(path => path, StringComparer.Ordinal).ToArray();
                if (files.Length == 0) throw new AudioTranscriptionException("No audio track could be extracted.");
                if (files.Length > maxChunks) throw new AudioTranscriptionException("Recording exceeds Audio : MaxChunks. No audio was uploaded.");
                if (files.Any(file => new FileInfo(file).Length > uploadLimit)) throw new AudioTranscriptionException("A converted segment exceeds the upload limit. Reduce Audio : ChunkSeconds. No audio was uploaded.");
            }
            else files = new[] { "audio" + extension }; // Direct uploads stay in memory.
            var segmentDuration = split ? chunkSeconds : 0;
            var segments = cached != null && cached.ChunkSeconds == segmentDuration && cached.Segments.Count < files.Length
                ? cached.Segments.ToList() : new List<AudioSegment>();
            for (var index = segments.Count; index < files.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                Console.WriteLine($"[CRAWL-URL] AUDIO | Transcribing segment {index + 1}/{files.Length} | {url}");
                var text = await TranscribeAsync(files[index], model, key, ct, split ? null : body).ConfigureAwait(false);
                segments.Add(new AudioSegment(index, split ? index * chunkSeconds : 0, text));
                // Save each segment so a canceled/failed recording can resume without
                // paying to transcribe its already completed segments again.
                var progressText = string.Join(Environment.NewLine + Environment.NewLine, segments.Select(segment => segment.Text));
                await _store.SaveAudioTranscriptAsync(new AudioTranscript(url, hashHex, model, progressText, segments.ToList(),
                    index == files.Length - 1, segmentDuration), ct).ConfigureAwait(false);
            }
            var fullText = string.Join(Environment.NewLine + Environment.NewLine, segments.Select(segment => segment.Text));
            Console.WriteLine($"[CRAWL-URL] AUDIO-COMPLETE | {segments.Count} segment(s), {fullText.Length} characters saved | {url}");
            return Payload(fullText, hash, url);
        }
        finally
        {
            try
            {
                if (directory != null) await RemoveTemporaryAudioAsync(directory).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }

    private static async Task RemoveTemporaryAudioAsync(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3)
                    throw new AudioTranscriptionException("Could not delete temporary audio after processing. Saved transcript text is retained; check file access to " + directory);
                // Cleanup must finish even when the transcription was canceled.
                await Task.Delay(100 * (attempt + 1), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static ProcessedPayload Payload(string text, byte[] hash, string source) => new()
    { Text = text, ContentHash = hash, Title = Path.GetFileName(Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.AbsolutePath : source) };

    private static string NormalizeExtension(string? extension, string? mime)
    {
        if (extension != null && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return extension.ToLowerInvariant();
        return mime?.ToLowerInvariant() switch
        { "audio/mpeg" or "audio/mp3" => ".mp3", "audio/mp4" or "audio/x-m4a" => ".m4a", "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav", "audio/webm" or "video/webm" => ".webm", "video/mp4" => ".mp4", _ => ".audio" };
    }

    private async Task ConvertAsync(string input, string directory, int seconds, int maxChunks, CancellationToken ct)
    {
        var executable = TrustedExecutable.Resolve(_config["Audio:FFmpegPath"], "ffmpeg.exe");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", input, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-c:a", "libmp3lame", "-b:a", "32k", "-t", ((long)seconds * maxChunks + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-f", "segment", "-segment_time", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-reset_timestamps", "1", Path.Combine(directory, "part-%05d.mp3") }) start.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config.GetValue<int>("Audio:ConversionTimeoutSeconds", 300), 1, 3600)));
        Process process;
        try { process = Process.Start(start) ?? throw new AudioTranscriptionException("Could not start FFmpeg."); }
        catch (System.ComponentModel.Win32Exception) { throw new AudioTranscriptionException("FFmpeg was not found. Set Audio : FFmpegPath in Settings to convert or split this recording."); }
        using (process)
        {
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new AudioTranscriptionException("Audio conversion timed out.", retryable: true);
            }
            await Task.WhenAll(error, output).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new AudioTranscriptionException("Audio conversion failed. Verify the file contains a supported audio track.");
        }
    }

    private async Task<string> TranscribeAsync(string file, string model, string key, CancellationToken ct, byte[]? body = null)
    {
        var attempts = Math.Clamp(_config.GetValue<int>("Audio:MaxAttempts", 3), 1, 5);
        for (var attempt = 1; ; attempt++)
        {
            if (Interlocked.Increment(ref _apiRequests) > Math.Max(1, _config.GetValue<int>("Audio:MaxApiRequestsPerRun", 100)))
                throw new AudioTranscriptionException("Audio API request limit reached for this run. Increase Audio : MaxApiRequestsPerRun or start a new run.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config.GetValue<int>("Audio:RequestTimeoutSeconds", 180), 1, 1800)));
            using var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(model), "model");
            multipart.Add(body == null ? new StreamContent(File.OpenRead(file)) : new ByteArrayContent(body), "file", Path.GetFileName(file));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions") { Content = multipart };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            try
            {
                using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var billing = OpenAiBilling.BillingError(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                    if (billing != null) { OpenAiBilling.Report(billing); throw new AudioTranscriptionException(billing); }
                }
                var transient = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
                if (transient && attempt < attempts)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 0, 60)), ct).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new AudioTranscriptionException($"Audio API returned HTTP {(int)response.StatusCode}. Check your key, model access and API quota.", transient);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                if (!json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new AudioTranscriptionException("Audio API returned an invalid transcript response.");
                OpenAiBilling.RequestSucceeded();
                return text.GetString() ?? string.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                if (attempt >= attempts) throw new AudioTranscriptionException("Audio transcription request timed out.", true);
            }
            catch (HttpRequestException)
            {
                if (attempt >= attempts) throw new AudioTranscriptionException("Could not connect to the audio transcription service.", true);
            }
            catch (JsonException) { throw new AudioTranscriptionException("Audio API returned invalid JSON."); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
        }
    }
}
