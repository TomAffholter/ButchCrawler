using Microsoft.Extensions.Configuration;
using System.IO;

namespace ButchCrawler.Operator;

public static class SupportOptions
{
    public const string ContactEmail = "HistoryCrawlerApp@gmail.com";
    public const string DefaultDonationUrl =
        "https://www.paypal.com/cgi-bin/webscr?cmd=_donations&business=HistoryCrawlerApp%40gmail.com&item_name=History+Crawler+donation&currency_code=USD";

    public static Uri GetDonationUri(string? settingsPath)
    {
        var value = DefaultDonationUrl;
        if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
        {
            var path = Path.GetFullPath(settingsPath);
            var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(path)!)
                .AddJsonFile(Path.GetFileName(path), optional: false)
                .AddJsonFile($"appsettings.{environment}.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
            try { value = configuration["Support:PayPalDonationUrl"] ?? DefaultDonationUrl; }
            finally { (configuration as IDisposable)?.Dispose(); }
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("paypal.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".paypal.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The contribution link must be a complete HTTPS address on paypal.com.");
        if (!string.Equals(QueryValue(uri, "business"), ContactEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The PayPal contribution page must direct payments to {ContactEmail}.");
        return uri;
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0].Replace("+", " "));
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;
            return pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace("+", " ")) : string.Empty;
        }
        return null;
    }
}
