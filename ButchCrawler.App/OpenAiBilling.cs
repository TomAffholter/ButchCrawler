using System.Text.Json;

namespace ButchCrawler.App;

public static class OpenAiBilling
{
    public const string BillingUrl = "https://platform.openai.com/settings/organization/billing";
    public const string UsageUrl = "https://platform.openai.com/settings/organization/usage";
    public const string LimitsUrl = "https://platform.openai.com/settings/organization/limits";
    public const string ChatGptUsageUrl = "https://chatgpt.com/settings/usage";
    public const string LogPrefix = "[OPENAI-BILLING] ";
    private static string? _lastNotice;
    public static string Status => Volatile.Read(ref _lastNotice) ?? "Balance not checked. Open API billing to see your available credits.";

    public static string? BillingError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return null;
            string? Read(string name) => error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var code = Read("code"); var type = Read("type");
            var reason = code switch
            {
                "credit_balance_exhausted" => "OpenAI API credits have run out. Add credits in API billing.",
                "organization_spend_limit_exceeded" or "project_spend_limit_exceeded" => "An OpenAI API spending limit was reached. Review organization/project limits.",
                "organization_usage_limit_exceeded" => "Your OpenAI API account usage limit was reached. Review your account limits.",
                "insufficient_quota" => "OpenAI API credits or account quota are unavailable. Check billing and limits.",
                _ => type == "insufficient_quota" ? "OpenAI API credits or account quota are unavailable. Check billing and limits." : null
            };
            return reason == null ? null : reason + " This request will not be retried automatically. About contains account links. " + BillingUrl;
        }
        catch (JsonException) { return null; }
    }
    public static void Report(string message)
    {
        Observe(LogPrefix + message);
        Console.WriteLine(LogPrefix + message);
    }
    public static void Observe(string line)
    {
        if (line.StartsWith(LogPrefix, StringComparison.Ordinal))
            Volatile.Write(ref _lastNotice, "Last API billing notice: " + line[LogPrefix.Length..] + "\nObserved " + DateTimeOffset.Now.ToString("g"));
    }
    public static void ClearNotice() => Volatile.Write(ref _lastNotice, null);
    public static void RequestSucceeded()
    {
        if (Interlocked.Exchange(ref _lastNotice, null) != null)
            Console.WriteLine(LogPrefix + "An API request succeeded after the earlier error. Check billing for the current balance.");
    }
}
