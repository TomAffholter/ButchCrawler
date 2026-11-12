namespace ButchCrawler.App;

public sealed record NewspaperRegion(string Name, IReadOnlyList<string> States);

public static class HistoricalNewspaperCatalog
{
    // Deliberately ordered for the novice Setup screen: SW, NW, MW, SE, NE, territories.
    public static readonly IReadOnlyList<NewspaperRegion> Regions = new[]
    {
        new NewspaperRegion("Southwest", new[] { "Arizona", "California", "Colorado", "Nevada", "New Mexico", "Oklahoma", "Texas", "Utah" }),
        new NewspaperRegion("Northwest", new[] { "Alaska", "Hawaii", "Idaho", "Montana", "Oregon", "Washington", "Wyoming" }),
        new NewspaperRegion("Midwest", new[] { "Illinois", "Indiana", "Iowa", "Kansas", "Michigan", "Minnesota", "Missouri", "Nebraska", "North Dakota", "Ohio", "South Dakota", "Wisconsin" }),
        new NewspaperRegion("Southeast", new[] { "Alabama", "Arkansas", "District of Columbia", "Florida", "Georgia", "Kentucky", "Louisiana", "Maryland", "Mississippi", "North Carolina", "South Carolina", "Tennessee", "Virginia", "West Virginia" }),
        new NewspaperRegion("Northeast", new[] { "Connecticut", "Delaware", "Maine", "Massachusetts", "New Hampshire", "New Jersey", "New York", "Pennsylvania", "Rhode Island", "Vermont" }),
        new NewspaperRegion("Territories", new[] { "Puerto Rico", "U.S. Virgin Islands" })
    };

    public static IReadOnlyList<string> AllStates => Regions.SelectMany(x => x.States).ToArray();

    public static string NormalizeState(string value) => value.Trim().ToLowerInvariant() switch
    {
        "district of columbia" or "washington, d.c." or "washington dc" => "District of Columbia",
        "puerto rico" => "Puerto Rico",
        "u.s. virgin islands" or "us virgin islands" or "virgin islands" => "U.S. Virgin Islands",
        var state => AllStates.FirstOrDefault(x => x.Equals(state, StringComparison.OrdinalIgnoreCase)) ?? value.Trim()
    };
}
