using System.Net;
using System.Net.Http;
using ButchCrawler.App;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class ReviewRegressionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var retryClassifier = typeof(CrawlerService).GetMethod("IsTransientStatusCode",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        foreach (var status in new int?[] { null, 0, 408, 425, 429, 500, 503, 599 })
            check((bool)retryClassifier.Invoke(null, new object?[] { status })!, "queue retries transient status " + (status?.ToString() ?? "missing"));
        foreach (var status in new[] { 200, 301, 400, 401, 403, 404, 410 })
            check(!(bool)retryClassifier.Invoke(null, new object?[] { (int?)status })!, "queue does not retry permanent status " + status);
        foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "172.16.0.1", "192.168.1.1", "169.254.169.254", "::1", "fc00::1", "::ffff:127.0.0.1" })
            check(!NetworkAccessPolicy.IsPublicAddress(IPAddress.Parse(address)), "deny non-public address " + address);
        check(NetworkAccessPolicy.IsPublicAddress(IPAddress.Parse("8.8.8.8")), "allow public IPv4");
        check(NetworkAccessPolicy.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111")), "allow public IPv6");

        foreach (var status in new[] { HttpStatusCode.ServiceUnavailable, HttpStatusCode.TooManyRequests, HttpStatusCode.Found })
        {
            var calls = 0;
            using var client = new HttpClient(new StubHandler(() =>
            {
                calls++;
                return new HttpResponseMessage(calls == 1 ? status : HttpStatusCode.OK)
                { Content = new StringContent("User-agent: *\nDisallow: /Private\nDisallow: /Secret*$\n") };
            }));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Crawler:RobotsFailClosed"] = "true" }).Build();
            using var robots = new RobotsManager(client, NullLogger<RobotsManager>.Instance, config);
            check(!await robots.IsAllowedAsync("https://example.test/", default), "robots fail-closed for " + status);
            check(!await robots.IsAllowedAsync("https://example.test/Private", default) && calls == 2,
                "robots failure does not cache allow-all for " + status);
            check(await robots.IsAllowedAsync("https://example.test/private", default), "robots literal paths preserve case");
            check(!await robots.IsAllowedAsync("https://example.test/SecretPage", default) &&
                await robots.IsAllowedAsync("https://example.test/secretPage", default), "robots wildcard paths preserve case");
        }

        var redirectCalls = 0;
        using (var redirectClient = new HttpClient(new StubHandler(() =>
        {
            redirectCalls++;
            if (redirectCalls == 1)
                return new HttpResponseMessage(HttpStatusCode.Found)
                { Headers = { Location = new Uri("https://www.example.test/robots.txt") } };
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("Sitemap: https://example.test/sitemap.xml\nUser-agent: *\nDisallow: /private\n") };
        })))
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Crawler:RobotsFailClosed"] = "true" }).Build();
            using var robots = new RobotsManager(redirectClient, NullLogger<RobotsManager>.Instance, config);
            check(!await robots.IsAllowedAsync("https://example.test/private", default) && redirectCalls == 2,
                "robots follows redirects before applying rules");
            var maps = await robots.GetSitemapsAsync("https://example.test/", default);
            check(maps.SequenceEqual(new[] { "https://example.test/sitemap.xml" }),
                "robots retains declared sitemap locations");
        }
        using var queue = new EmbeddingIndexQueue(1);
        await queue.EnqueueAsync(new IndexRequest("a", null, "first", null));
        using var cancel = new CancellationTokenSource();
        var pending = queue.EnqueueAsync(new IndexRequest("b", null, "second", null), cancel.Token).AsTask();
        check(!pending.IsCompleted, "full index queue applies backpressure");
        cancel.Cancel();
        try { await pending; throw new Exception("Expected indexing cancellation"); }
        catch (OperationCanceledException) { check(true, "blocked index enqueue supports cancellation"); }
    }

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response());
    }
}
