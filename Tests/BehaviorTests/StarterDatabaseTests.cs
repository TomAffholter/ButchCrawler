using ButchCrawler.Operator;

internal static class StarterDatabaseTests
{
    public static void Run(Action<bool, string> check)
    {
        var plain = StarterDatabaseService.Assess("https://archive.example.org/collections/letters/");
        check(plain.Recommended, "starter database recommends a successful stable URL");

        var query = StarterDatabaseService.Assess("https://archive.example.org/item?id=42");
        check(!query.Recommended && query.Reason.Contains("query", StringComparison.OrdinalIgnoreCase),
            "starter database leaves query URLs for human review");

        var login = StarterDatabaseService.Assess("https://archive.example.org/account/signin?token=secret");
        check(!login.Recommended && login.Reason.Contains("login", StringComparison.OrdinalIgnoreCase),
            "starter database excludes login and session-like URLs by default");

        var generated = StarterDatabaseService.Assess("https://archive.example.org/search?q=Lincoln&page=4");
        check(!generated.Recommended && generated.Reason.Contains("search", StringComparison.OrdinalIgnoreCase),
            "starter database excludes generated search and paging URLs by default");

        var local = StarterDatabaseService.Assess("file:///C:/Research/private.pdf");
        check(!local.Recommended, "starter database never promotes local file paths as web seeds");
    }
}
