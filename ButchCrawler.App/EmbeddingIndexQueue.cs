using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ButchCrawler.App
{
    // Small request object for indexing.
    public record IndexRequest(string Url, string? Title, string Text, string? ContentHash);

    public interface IEmbeddingIndexQueue
    {
        ValueTask EnqueueAsync(IndexRequest request, CancellationToken cancellationToken = default);
        ChannelReader<IndexRequest> Reader { get; }
    }

    public class EmbeddingIndexQueue : IEmbeddingIndexQueue, IDisposable
    {
        private readonly Channel<IndexRequest> _channel;
        private bool _disposed;

        public EmbeddingIndexQueue(int capacity = 1000)
        {
            // Bounded to provide backpressure.
            var options = new BoundedChannelOptions(capacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            };
            _channel = Channel.CreateBounded<IndexRequest>(options);
        }

        public ChannelReader<IndexRequest> Reader => _channel.Reader;

        public ValueTask EnqueueAsync(IndexRequest request, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EmbeddingIndexQueue));
            return _channel.Writer.WriteAsync(request, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _channel.Writer.TryComplete();
        }
    }
}