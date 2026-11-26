using System.Net.Mail;
using System.IO;
using System.Text.Json;

namespace ButchCrawler.Operator;

public sealed class CommunitySubmission
{
    public int Version { get; set; } = 1;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;
    public string ResearchType { get; set; } = string.Empty;
    public string Headline { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool ConsentGiven { get; set; }
    public bool MayContact { get; set; }
    public DateTime SubmittedUtc { get; set; } = DateTime.UtcNow;
}

public static class CommunitySubmissionCodec
{
    public const string BeginMarker = "--- HISTORY CRAWLER IMPORT BEGIN ---";
    public const string EndMarker = "--- HISTORY CRAWLER IMPORT END ---";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string Subject(CommunitySubmission submission) => SubjectForType(submission.Type);

    public static string SubjectForType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "subscription" => "[History Crawler Subscription] Update request",
        "suggestion" => "[History Crawler Suggestion] Improvement idea",
        "bug" => "[History Crawler Bug] Bug or anomaly report",
        _ => "[History Crawler Message] Support request"
    };

    public static string CreateEmailBody(CommunitySubmission submission)
    {
        Validate(submission);
        var kind = submission.Type switch
        {
            "subscription" => "Subscription request",
            "bug" => "Bug or anomaly report",
            _ => "Suggestion"
        };
        var details = new List<string>
        {
            kind,
            string.Empty,
            $"Headline: {Display(submission.Headline)}",
            string.Empty,
            $"Name: {submission.Name}",
            $"Email: {submission.Email}",
            $"Organization: {Display(submission.Organization)}",
            $"Type of research: {submission.ResearchType}"
        };
        if (submission.Type == "suggestion") details.Add($"Suggestion category: {submission.Category}");
        if (submission.Type == "bug") details.Add($"Problem area: {submission.Category}");
        details.Add(submission.Type switch
        {
            "subscription" => $"Comments: {Display(submission.Message)}",
            "bug" => $"What happened: {submission.Message}",
            _ => $"Suggestion: {submission.Message}"
        });
        details.Add(submission.Type switch
        {
            "subscription" => $"Subscription consent: {(submission.ConsentGiven ? "Yes" : "No")}",
            "bug" => $"May contact about this report: {(submission.MayContact ? "Yes" : "No")}",
            _ => $"May contact about this suggestion: {(submission.MayContact ? "Yes" : "No")}"
        });
        details.Add($"Submitted (UTC): {submission.SubmittedUtc:O}");
        details.Add(string.Empty);
        details.Add("This message was prepared by the History Crawler support form. Review the readable information above before relying on the structured reference section below.");
        details.Add(string.Empty);
        details.Add(BeginMarker);
        details.Add(JsonSerializer.Serialize(submission));
        details.Add(EndMarker);
        return string.Join(Environment.NewLine, details);
    }

    public static CommunitySubmission ParseEmail(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Copy the complete History Crawler support email first.");
        if (text.Length > 100_000) throw new InvalidDataException("The copied email is too large to be a History Crawler response.");
        var begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0) throw new InvalidDataException("The copied text does not contain a History Crawler import section.");
        begin += BeginMarker.Length;
        var end = text.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException("The History Crawler import section is incomplete.");
        var json = text[begin..end].Trim();
        CommunitySubmission? submission;
        try { submission = JsonSerializer.Deserialize<CommunitySubmission>(json, JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("The History Crawler import section is damaged or incomplete.", ex); }
        if (submission is null) throw new InvalidDataException("The History Crawler import section is empty.");
        Validate(submission);
        return submission;
    }

    public static void Validate(CommunitySubmission submission)
    {
        submission.Type = Normalize(submission.Type, 20).ToLowerInvariant();
        submission.Name = Normalize(submission.Name, 200);
        submission.Email = Normalize(submission.Email, 320);
        submission.Organization = Normalize(submission.Organization, 300);
        submission.ResearchType = Normalize(submission.ResearchType, 1000);
        submission.Headline = Normalize(submission.Headline, 200);
        submission.Category = Normalize(submission.Category, 100);
        submission.Message = Normalize(submission.Message, 4000);
        if (submission.Version != 1) throw new InvalidDataException("This response was created by an unsupported History Crawler form version.");
        if (submission.Type is not ("subscription" or "suggestion" or "bug")) throw new InvalidDataException("The response type is not recognized.");
        if (submission.Name.Length == 0) throw new InvalidDataException("Name is required.");
        if (!MailAddress.TryCreate(submission.Email, out _)) throw new InvalidDataException("Enter a valid email address.");
        if (submission.ResearchType.Length == 0) throw new InvalidDataException("Type of research is required.");
        if (submission.Type == "subscription" && !submission.ConsentGiven) throw new InvalidDataException("Subscription consent is required.");
        if (submission.Type == "suggestion" && submission.Message.Length == 0) throw new InvalidDataException("Enter your suggestion.");
        if (submission.Type == "bug" && submission.Message.Length == 0) throw new InvalidDataException("Describe the bug or anomaly.");
        if (submission.SubmittedUtc == default || submission.SubmittedUtc > DateTime.UtcNow.AddDays(1) || submission.SubmittedUtc < new DateTime(2020, 1, 1))
            submission.SubmittedUtc = DateTime.UtcNow;
    }

    private static string Normalize(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Length <= maximum ? value.Trim() : value.Trim()[..maximum];

    private static string Display(string value) => value.Length == 0 ? "(not provided)" : value;
}
