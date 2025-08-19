using System.Threading.Channels;

namespace SmartData.Core.Queue
{
    public interface ISmartDataQueue
    {
        void Enqueue(SmartWorkItem item);
        IAsyncEnumerable<SmartWorkItem> DequeueAllAsync(CancellationToken ct);
    }

    public sealed class SmartDataQueue : ISmartDataQueue
    {
        private readonly Channel<SmartWorkItem> _channel;

        public SmartDataQueue()
        {
            _channel = Channel.CreateUnbounded<SmartWorkItem>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false
            });
        }

        public void Enqueue(SmartWorkItem item) => _channel.Writer.TryWrite(item);

        public IAsyncEnumerable<SmartWorkItem> DequeueAllAsync(CancellationToken ct) =>
            _channel.Reader.ReadAllAsync(ct);
    }
}
