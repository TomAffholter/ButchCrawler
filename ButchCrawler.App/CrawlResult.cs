using System;
using System.Collections.Generic;

namespace ButchCrawler.App
{
    public class CrawlResult
    {
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string RawHtml { get; set; } = string.Empty;

        // Outgoing links discovered on the page (absolute URLs)
        public List<string> Links { get; set; } = new List<string>();

        // Structured data
        public List<string> JsonLd { get; set; } = new List<string>();
        public Dictionary<string, string> OpenGraph { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<Dictionary<string, string>> Microformats { get; set; } = new List<Dictionary<string, string>>();

        // Content hash used to detect meaningful changes
        public string ContentHash { get; set; } = string.Empty;
    }
}