using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ButchCrawler.App
{
    public class PiiRedactor : IPiiRedactor
    {
        private readonly PiiRedactionOptions _opts;
        private readonly ILogger<PiiRedactor> _logger;

        // Common regexes (compiled) for detection
        private static readonly Regex EmailRegex = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
        private static readonly Regex SsnRegex = new(@"\b\d{3}-\d{2}-\d{4}\b|\b\d{9}\b", RegexOptions.Compiled);
        // Rough credit card regex: 13-19 digits possibly separated by spaces or dashes
        private static readonly Regex CreditCardRegex = new(@"(?<!\d)(?:\d[ -]*?){13,19}(?!\d)", RegexOptions.Compiled);

        public PiiRedactor(IConfiguration config, ILogger<PiiRedactor> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _opts = new PiiRedactionOptions();
            var section = config.GetSection("Crawler:Pii");
            if (section.Exists())
            {
                section.Bind(_opts);
            }
        }

        public string Redact(string? text)
        {
            return RedactWithReport(text).RedactedText;
        }

        public (string RedactedText, PiiRedactionReport Report) RedactWithReport(string? text)
        {
            var report = new PiiRedactionReport(false, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>());
            if (!_opts.Enabled) return (text ?? string.Empty, report);

            var src = text ?? string.Empty;
            var detectedTypes = new System.Collections.Generic.List<string>();
            var matchHashes = new System.Collections.Generic.List<string>();
            int emailCount = 0, ssnCount = 0, ccCount = 0;

            try
            {
                if (_opts.RedactEmails)
                {
                    var matches = EmailRegex.Matches(src);
                    emailCount = matches.Count;
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        var h = ComputeHash(m.Value);
                        matchHashes.Add(h);
                    }
                    if (emailCount > 0) detectedTypes.Add("email");
                    src = EmailRegex.Replace(src, _opts.EmailReplacement);
                }

                if (_opts.RedactSsns)
                {
                    var matches = SsnRegex.Matches(src);
                    ssnCount = matches.Count;
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        var h = ComputeHash(m.Value);
                        matchHashes.Add(h);
                    }
                    if (ssnCount > 0) detectedTypes.Add("ssn");
                    src = SsnRegex.Replace(src, _opts.SsnReplacement);
                }

                if (_opts.RedactCreditCards)
                {
                    var matches = CreditCardRegex.Matches(src);
                    ccCount = matches.Count;
                    foreach (System.Text.RegularExpressions.Match m in matches)
                    {
                        var h = ComputeHash(m.Value);
                        matchHashes.Add(h);
                    }
                    if (ccCount > 0) detectedTypes.Add("creditcard");
                    if (_opts.CreditCardMaskLast4)
                    {
                        src = CreditCardRegex.Replace(src, m => MaskCreditCard(m.Value));
                    }
                    else
                    {
                        src = CreditCardRegex.Replace(src, _opts.CreditCardReplacement);
                    }
                }

                var any = emailCount + ssnCount + ccCount > 0;
                report = new PiiRedactionReport(any, emailCount, ssnCount, ccCount, detectedTypes.ToArray(), matchHashes.ToArray());
                return (src, report);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PII redaction failed; returning original text");
                return (text ?? string.Empty, report);
            }
        }

        private string MaskCreditCard(string raw)
        {
            // Remove non-digit chars
            var digits = System.Text.RegularExpressions.Regex.Replace(raw, "\\D", "");
            if (digits.Length <= 4)
                return _opts.CreditCardReplacement;
            var last4 = digits.Substring(digits.Length - 4);
            return "[REDACTED-CREDITCARD-****-" + last4 + "]";
        }

        private static string ComputeHash(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var hash = sha.ComputeHash(bytes);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }
    }
}
