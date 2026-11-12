using System;
using System.Threading;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    public class ProcessedPayload
    {
        public byte[] ContentHash { get; set; } = Array.Empty<byte>();
        public string Text { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? StructuredFormat { get; set; }
        public string? StructuredCategory { get; set; }
        public long? StructuredRecordCount { get; set; }
        public int? StructuredTableCount { get; set; }
        public int? StructuredEntryCount { get; set; }
        public bool StructuredTruncated { get; set; }
        public string? StructuredWarning { get; set; }
    }

    public interface IPayloadProcessor
    {
        Task<ProcessedPayload> ProcessAsync(string url, byte[] body, string? contentType, string? extension, CancellationToken ct);
    }
}
