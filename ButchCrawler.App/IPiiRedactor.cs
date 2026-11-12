namespace ButchCrawler.App
{
    public record PiiRedactionReport(bool AnyRedacted, int EmailCount, int SsnCount, int CreditCardCount, string[] DetectedTypes, string[] MatchHashes);

    public interface IPiiRedactor
    {
        /// <summary>
        /// Redact PII from the provided text according to configured rules.
        /// Never returns null; returns original text if redaction disabled or nothing matched.
        /// </summary>
        string Redact(string? text);

        /// <summary>
        /// Redact text and return a report describing detected/removed PII.
        /// The report MUST NOT contain raw PII values; MatchHashes contains SHA256 hashes of matches.
        /// </summary>
        (string RedactedText, PiiRedactionReport Report) RedactWithReport(string? text);
    }
}
