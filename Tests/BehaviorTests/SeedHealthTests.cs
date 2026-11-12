using System.Reflection;
using ButchCrawler.App;

internal static class SeedHealthTests
{
    public static void Run(Action<bool, string> check)
    {
        check(SeedHealthSchema.Sql.Contains("HealthStatus", StringComparison.Ordinal) &&
              SeedHealthSchema.Sql.Contains("ConsecutiveFailures", StringComparison.Ordinal) &&
              SeedHealthSchema.Sql.Contains("FirstFailureAt", StringComparison.Ordinal) &&
              !SeedHealthSchema.Sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase),
            "seed health schema is additive and preserves existing seeds");

        check(HumanVerificationSessionStore.GetStorageStatePath("https://example.org/one") ==
              HumanVerificationSessionStore.GetStorageStatePath("https://example.org/two"),
            "human-verification browser sessions are scoped to a site");
        check(HumanVerificationSessionStore.LooksLikeChallenge("<html><title>Just a moment...</title><body>Please verify you are human</body></html>") &&
              !HumanVerificationSessionStore.LooksLikeChallenge("<html><title>Archive</title><body>Historical records and documents</body></html>"),
            "human-verification pages are distinguished from normal content");

        var classify = typeof(CrawlerService).GetMethod("ClassifySeedFailure", BindingFlags.Static | BindingFlags.NonPublic)!;
        object Policy(int status, string reason) => classify.Invoke(null, new object[] { status, reason })!;
        bool Deactivates(object policy) => (bool)policy.GetType().GetProperty("Deactivate")!.GetValue(policy)!;
        string Status(object policy) => (string)policy.GetType().GetProperty("HealthStatus")!.GetValue(policy)!;
        DateTime? Next(object policy) => (DateTime?)policy.GetType().GetProperty("NextCheckUtc")!.GetValue(policy);

        var gone = Policy(410, "Gone");
        check(!Deactivates(gone) && Status(gone) == "gone" && Next(gone) == null,
            "HTTP 410 is stored for user review without automatic disabling or deletion");
        var challenge = Policy(403, "Challenge detected: cloudflare_turnstile");
        check(!Deactivates(challenge) && Status(challenge) == "human-verification" && Next(challenge) == null,
            "human verification waits for the user without pausing or automatic retries");
        var forbidden = Policy(403, "HTTP 403 Forbidden");
        check(!Deactivates(forbidden) && Status(forbidden) == "blocked" && Next(forbidden) == null,
            "HTTP 403 is stored for manual review without repeated requests");
        foreach (var (code, expected) in new[] { (404, "not-found"), (429, "rate-limited"), (503, "temporarily-unavailable") })
        {
            var policy = Policy(code, "test failure");
            check(!Deactivates(policy) && Status(policy) == expected && Next(policy) > DateTime.UtcNow,
                $"HTTP {code} remains recoverable and receives a later health check");
        }
    }
}
