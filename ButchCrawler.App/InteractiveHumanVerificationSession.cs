using Microsoft.Playwright;

namespace ButchCrawler.App;

public sealed record HumanVerificationResult(bool Success, string Message, string FinalUrl);

/// <summary>A user-controlled, visible browser used only to complete site verification.</summary>
public sealed class InteractiveHumanVerificationSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly string _originalUrl;

    private InteractiveHumanVerificationSession(IPlaywright playwright, IBrowser browser,
        IBrowserContext context, IPage page, string originalUrl)
    {
        _playwright = playwright; _browser = browser; _context = context; _page = page; _originalUrl = originalUrl;
    }

    public static async Task<InteractiveHumanVerificationSession> OpenAsync(string url, string? userAgent = null,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Select a valid HTTP or HTTPS seed URL.", nameof(url));
        if (!await NetworkAccessPolicy.IsPublicUriAsync(url, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Human verification is limited to public websites.");

        var playwright = await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false);
        IBrowser? browser = null; IBrowserContext? context = null;
        try
        {
            try
            {
                // Human checks should be completed in the installed, current Edge
                // browser rather than Playwright's bundled Chromium build. This is
                // still a controlled browser, so a site may legitimately decline it.
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                { Headless = false, Channel = "msedge" }).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = false }).ConfigureAwait(false);
            }

            var options = new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 850 },
                IgnoreHTTPSErrors = false
            };
            var readableState = HumanVerificationSessionStore.CreateReadableStorageStateFile(url);
            if (readableState != null) options.StorageStatePath = readableState;
            try { context = await browser.NewContextAsync(options).ConfigureAwait(false); }
            finally { if (readableState != null) try { File.Delete(readableState); } catch { } }
            var page = await context.NewPageAsync().ConfigureAwait(false);
            await page.RouteAsync("**/*", async route =>
            {
                if (!Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var target))
                { await route.AbortAsync("blockedbyclient").ConfigureAwait(false); return; }
                if (target.Scheme is "data" or "blob" or "about") { await route.ContinueAsync().ConfigureAwait(false); return; }
                if (target.Scheme is not ("http" or "https") ||
                    !await NetworkAccessPolicy.IsPublicUriAsync(route.Request.Url, ct).ConfigureAwait(false))
                { await route.AbortAsync("blockedbyclient").ConfigureAwait(false); return; }
                await route.ContinueAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
            await page.GotoAsync(url, new PageGotoOptions { Timeout = 120_000, WaitUntil = WaitUntilState.DOMContentLoaded })
                .ConfigureAwait(false);
            return new InteractiveHumanVerificationSession(playwright, browser, context, page, url);
        }
        catch
        {
            if (context != null) try { await context.CloseAsync().ConfigureAwait(false); } catch { }
            if (browser != null) try { await browser.CloseAsync().ConfigureAwait(false); } catch { }
            playwright.Dispose();
            throw;
        }
    }

    public async Task<HumanVerificationResult> SaveAsync()
    {
        if (_page.IsClosed) return new(false, "The verification browser was closed. Open it again and complete the site check.", _originalUrl);
        var html = await _page.ContentAsync().ConfigureAwait(false);
        var finalUrl = _page.Url;
        if (HumanVerificationSessionStore.LooksLikeChallenge(html, finalUrl))
            return new(false, "The site still appears to be showing human verification. Complete it in Edge, then try Continue again.", finalUrl);

        var temporary = Path.Combine(Path.GetTempPath(), "ButchCrawler-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await _context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = temporary }).ConfigureAwait(false);
            HumanVerificationSessionStore.SaveEncrypted(_originalUrl, temporary);
            // Redirects commonly move between the bare host and www (or another
            // canonical host). Make the same verified state available to that
            // exact final host as well, without sharing it with unrelated sites.
            if (Uri.TryCreate(_originalUrl, UriKind.Absolute, out var original) &&
                Uri.TryCreate(finalUrl, UriKind.Absolute, out var final) &&
                !string.Equals(original.IdnHost, final.IdnHost, StringComparison.OrdinalIgnoreCase))
                HumanVerificationSessionStore.SaveEncrypted(finalUrl, temporary);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        return new(true, "Verification saved temporarily. Start the crawler to retry this site.", finalUrl);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _context.CloseAsync().ConfigureAwait(false); } catch { }
        try { await _browser.CloseAsync().ConfigureAwait(false); } catch { }
        _playwright.Dispose();
    }
}
