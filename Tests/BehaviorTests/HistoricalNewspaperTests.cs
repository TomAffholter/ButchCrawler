using System.Text.Json;
using ButchCrawler.App;

internal static class HistoricalNewspaperTests
{
    public static void Run(Action<bool, string> check)
    {
        check(HistoricalNewspaperCatalog.Regions.Select(x => x.Name).SequenceEqual(
            new[] { "Southwest", "Northwest", "Midwest", "Southeast", "Northeast", "Territories" }),
            "historical newspaper states are grouped in the requested regional order");
        check(HistoricalNewspaperCatalog.AllStates.Count == 53 &&
              HistoricalNewspaperCatalog.AllStates.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 53,
            "catalog includes all states, District of Columbia, and two territories exactly once");

        const string json = """
        {"results":[
          {"url":"https://www.loc.gov/resource/sn-test/?sp=1","title":"Utah paper","date":"1900-01-02","location_state":["Utah"],"description":["Butch Cassidy was mentioned."]},
          {"url":"https://www.loc.gov/resource/sn-other/?sp=2","title":"Maine paper","date":"1901","location_state":["Maine"],"description":["Not selected."]}
        ]}
        """;
        using var document = JsonDocument.Parse(json);
        var selected = new HashSet<string>(new[] { "Utah" }, StringComparer.OrdinalIgnoreCase);
        var results = HistoricalNewspaperDiscoveryService.ParseResults(document.RootElement, "Butch Cassidy", selected);
        check(results.Count == 1 && results[0].State == "Utah" &&
              results[0].Snippet.Contains("Butch Cassidy", StringComparison.Ordinal),
            "Library of Congress results retain OCR text and honor selected states");

        var key1 = HistoricalNewspaperDiscoveryService.ComputeQueryKey("Butch Cassidy", new[] { "Utah", "Colorado" });
        var key2 = HistoricalNewspaperDiscoveryService.ComputeQueryKey(" butch cassidy ", new[] { "Colorado", "Utah" });
        check(key1 == key2 && key1.Length == 64, "newspaper search checkpoint keys are stable and order independent");
    }
}
