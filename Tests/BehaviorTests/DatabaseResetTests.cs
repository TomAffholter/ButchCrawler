using ButchCrawler.Operator;

internal static class DatabaseResetTests
{
    public static void Run(Action<bool, string> check)
    {
        check(DatabaseResetService.IsPreservedTable("dbo", "SeedUrls", clearKeywords: false),
            "new-project reset protects Seed URLs when keywords are kept");
        check(DatabaseResetService.IsPreservedTable("dbo", "SeedUrls", clearKeywords: true),
            "new-project reset protects Seed URLs when keywords are cleared");
        check(DatabaseResetService.IsPreservedTable("dbo", "SeedKeywords", clearKeywords: false) &&
              !DatabaseResetService.IsPreservedTable("dbo", "SeedKeywords", clearKeywords: true),
            "Seed Keywords follow the explicit keep-or-clear choice");
        check(DatabaseResetService.IsPreservedTable("dbo", "SchemaVersion", clearKeywords: true),
            "new-project reset protects schema-version records");
        check(DatabaseResetService.IsPreservedTable("dbo", "HistoryCrawlerSubscriptions", clearKeywords: true) &&
              DatabaseResetService.IsPreservedTable("dbo", "HistoryCrawlerSuggestions", clearKeywords: true),
            "new-project reset protects received subscriptions and suggestions");
        check(!DatabaseResetService.IsPreservedTable("dbo", "CrawlResults", clearKeywords: false) &&
              !DatabaseResetService.IsPreservedTable("research", "SeedUrls", clearKeywords: false),
            "only the intended dbo seed tables are protected");
        check(DatabaseResetService.ConfirmationPhrase == "START NEW",
            "new-project reset requires the documented typed confirmation");
    }
}
