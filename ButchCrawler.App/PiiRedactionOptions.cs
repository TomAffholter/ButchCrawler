namespace ButchCrawler.App
{
    public class PiiRedactionOptions
    {
        public bool Enabled { get; set; } = true;
        public bool RedactEmails { get; set; } = true;
        public bool RedactSsns { get; set; } = true;
        public bool RedactCreditCards { get; set; } = true;
        public bool CreditCardMaskLast4 { get; set; } = true;
        public string EmailReplacement { get; set; } = "[REDACTED-EMAIL]";
        public string SsnReplacement { get; set; } = "[REDACTED-SSN]";
        public string CreditCardReplacement { get; set; } = "[REDACTED-CREDITCARD]";
    }
}
