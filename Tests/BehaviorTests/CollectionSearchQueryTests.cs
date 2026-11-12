using ButchCrawler.App;

internal static class CollectionSearchQueryTests
{
    public static void Run(Action<bool, string> check)
    {
        var simple = CollectionSearchQuery.Parse("Lincoln railroad Illinois");
        check(!simple.UsesBooleanLogic && simple.Terms.SequenceEqual(new[] { "Lincoln", "railroad", "Illinois" }) && simple.SqlPredicate.Contains(" OR "),
            "plain collection search keeps novice-friendly ranked OR behavior");

        var and = CollectionSearchQuery.Parse("Lincoln AND railroad");
        check(and.UsesBooleanLogic && and.SqlPredicate.Contains(" AND ") && and.Terms.SequenceEqual(new[] { "Lincoln", "railroad" }),
            "collection search supports AND");

        var or = CollectionSearchQuery.Parse("Lincoln OR Douglas");
        check(or.SqlPredicate.Contains(" OR ") && or.Terms.SequenceEqual(new[] { "Lincoln", "Douglas" }),
            "collection search supports OR");

        var not = CollectionSearchQuery.Parse("Lincoln NOT Nebraska");
        check(not.SqlPredicate.Contains(" AND NOT ") && not.PositiveTermIndexes.SequenceEqual(new[] { 0 }),
            "collection search supports friendly NOT shorthand without ranking excluded text");

        var grouped = CollectionSearchQuery.Parse("(Lincoln OR Douglas) AND debate");
        check(grouped.SqlPredicate.Contains(" OR ") && grouped.SqlPredicate.Contains(" AND ") && grouped.Terms.Count == 3,
            "collection search supports Boolean parentheses");

        var phrase = CollectionSearchQuery.Parse("\"Gettysburg Address\"");
        check(phrase.Terms.Count == 1 && phrase.Terms[0] == "Gettysburg Address",
            "collection search preserves a quoted exact phrase as one parameter");

        try
        {
            CollectionSearchQuery.Parse("Lincoln AND (Douglas OR");
            throw new Exception("Expected invalid Boolean query rejection");
        }
        catch (CollectionSearchQueryException)
        {
            check(true, "collection search explains malformed Boolean expressions");
        }
    }
}
