using System;

namespace ButchCrawler.App
{
    public class RenderResult
    {
        public string Url { get; set; } = string.Empty;
        public string FinalUrl { get; set; } = string.Empty;
        public string Html { get; set; } = string.Empty;
        public byte[]? Screenshot { get; set; }
        public byte[]? TraceZip { get; set; }
        public int StatusCode { get; set; }
        public string? Error { get; set; }
        public bool Success => string.IsNullOrWhiteSpace(Error) && StatusCode is >= 200 and < 400 && !string.IsNullOrWhiteSpace(Html);
    }
}
