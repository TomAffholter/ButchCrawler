using System.Text.Json.Nodes;
using System.IO;
using ButchCrawler.Operator;

internal static class CommunityResponseTests
{
    public static void Run(Action<bool, string> check)
    {
        var subscription = new CommunitySubmission
        {
            Type = "subscription",
            Name = "Jane Researcher",
            Email = "jane@example.com",
            Organization = "Historical Society",
            ResearchType = "Western newspapers",
            Headline = "Please send occasional release notes",
            Message = "Interested in OCR updates",
            ConsentGiven = true,
            SubmittedUtc = new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc)
        };
        var body = CommunitySubmissionCodec.CreateEmailBody(subscription);
        var parsed = CommunitySubmissionCodec.ParseEmail("Mail header\r\n" + body + "\r\nMail footer");
        check(parsed.Type == "subscription" && parsed.Email == "jane@example.com" && parsed.ConsentGiven &&
              parsed.Headline == "Please send occasional release notes",
            "subscription email round-trips through the structured import section");
        check(CommunitySubmissionCodec.Subject(subscription) == "[History Crawler Subscription] Update request",
            "subscription messages use a stable rule-friendly subject");

        var suggestion = new CommunitySubmission
        {
            Type = "suggestion", Name = "Sam", Email = "sam@example.com", ResearchType = "Genealogy",
            Headline = "Date filtering would help", Category = "Search", Message = "Add a date filter", MayContact = true
        };
        var suggestionBody = CommunitySubmissionCodec.CreateEmailBody(suggestion);
        check(CommunitySubmissionCodec.ParseEmail(suggestionBody).Message == "Add a date filter" &&
              suggestionBody.Contains("Headline: Date filtering would help", StringComparison.Ordinal),
            "suggestion email retains the user's internal headline and message");
        check(CommunitySubmissionCodec.Subject(suggestion) == "[History Crawler Suggestion] Improvement idea",
            "suggestion messages use a distinct rule-friendly subject");
        check(!suggestionBody.Contains("Record received responses", StringComparison.OrdinalIgnoreCase),
            "outgoing support messages do not direct users to administrative response management");

        var bug = new CommunitySubmission
        {
            Type = "bug", Name = "Alex", Email = "alex@example.com", ResearchType = "Local archives",
            Headline = "Incorrect PDF page count", Category = "Wrong or unexpected result",
            Message = "The PDF page count was incorrect.", MayContact = true
        };
        var bugBody = CommunitySubmissionCodec.CreateEmailBody(bug);
        check(CommunitySubmissionCodec.Subject(bug) == "[History Crawler Bug] Bug or anomaly report" &&
              CommunitySubmissionCodec.ParseEmail(bugBody).Type == "bug" &&
              bugBody.Contains("Headline: Incorrect PDF page count", StringComparison.Ordinal) &&
              bugBody.Contains("Problem area: Wrong or unexpected result", StringComparison.Ordinal),
            "bug reports have a dedicated subject and retain the problem details");

        var rejected = false;
        try { CommunitySubmissionCodec.ParseEmail("ordinary email without an import section"); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "ordinary emails cannot be imported as trusted response records");

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine("ButchCrawler.App", "appsettings.json")))!;
        var donation = settings["Support"]?["PayPalDonationUrl"]?.GetValue<string>() ?? string.Empty;
        check(Uri.TryCreate(donation, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
              uri.Host.EndsWith("paypal.com", StringComparison.OrdinalIgnoreCase) &&
              Uri.UnescapeDataString(uri.Query).Contains("business=HistoryCrawlerApp@gmail.com", StringComparison.OrdinalIgnoreCase),
            "configured contribution address is an HTTPS PayPal donation page for the History Crawler account");
        check(AppIdentity.Email == "HistoryCrawlerApp@gmail.com" && AppIdentity.Copyright.Contains("Tom Affholter, 2026", StringComparison.Ordinal),
            "public support email and copyright are current");
        check(SupportContactWindow.SubscriptionSubject == "Subscribe" &&
              SupportContactWindow.BugSubject == "Bug Report" &&
              SupportContactWindow.SuggestionSubject == "Suggestion",
            "simple support links use the requested email subjects");

        var mainWindow = File.ReadAllText(Path.Combine("ButchCrawler.Operator", "MainWindow.xaml"));
        check(mainWindow.Contains("Updates, bugs, and suggestions", StringComparison.OrdinalIgnoreCase) &&
              !mainWindow.Contains("Subscribe for updates", StringComparison.OrdinalIgnoreCase) &&
              !mainWindow.Contains("Send a suggestion", StringComparison.OrdinalIgnoreCase) &&
              !mainWindow.Contains("Report a bug or anomaly", StringComparison.OrdinalIgnoreCase),
            "the Support menu uses one simple contact page instead of three separate forms");

        var schema = CommunityResponseRepository.SchemaSql;
        check(schema.Contains("HistoryCrawlerSubscriptions", StringComparison.Ordinal) && schema.Contains("HistoryCrawlerSuggestions", StringComparison.Ordinal) &&
              schema.Contains("MessageHash", StringComparison.Ordinal),
            "subscription and suggestion tables include duplicate protection");
    }
}
