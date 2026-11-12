using System;

namespace ButchCrawler.App
{
    public class CaptchaTask
    {
        public long Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string? SeedUrl { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Status { get; set; } = "pending"; // pending|solved|ignored
        public string? ScreenshotPath { get; set; }
        public string? HtmlSnippet { get; set; }
        public string? SolutionJson { get; set; }
        public DateTime? SolvedAt { get; set; }
        public int Attempts { get; set; }
    }
}
