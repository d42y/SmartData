using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SmartData.Core.Queue
{
    public sealed class Lane<TItem> where TItem : SmartItem
    {
        private readonly int _partitions;
        private readonly Channel<TItem>[] _channels;
        private readonly Func<TItem, string> _keySelector;
        private readonly Func<TItem, int> _partitioner;
        private readonly ILaneStrategy<TItem> _strategy;
        private readonly CancellationToken _appStop;
        private readonly List<Task> _workers = new();
        private readonly ConcurrentDictionary<int, Dictionary<string, TItem>> _inflight = new();

        public Lane(
            int partitions,
            int capacityPerPartition,
            Func<TItem, string> keySelector,
            Func<TItem, int> partitioner,
            ILaneStrategy<TItem> strategy,
            CancellationToken appStop)
        {
            _partitions = partitions;
            _channels = new Channel<TItem>[partitions];
            _keySelector = keySelector;
            _partitioner = partitioner;
            _strategy = strategy;
            _appStop = appStop;

            for (int p = 0; p < partitions; p++)
            {
                _channels[p] = Channel.CreateBounded<TItem>(new BoundedChannelOptions(capacityPerPartition)
                {
                    SingleReader = true,   // preserve order within partition
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait
                });
                _inflight[p] = new Dictionary<string, TItem>(StringComparer.Ordinal);
                _workers.Add(RunPartitionAsync(p));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int EnsurePositiveHash(int h) => h & 0x7fffffff;

        public ValueTask EnqueueAsync(TItem item, CancellationToken ct)
        {
            var p = _partitioner(item);
            return _channels[p].Writer.WriteAsync(item, ct);
        }

        public void Complete()
        {
            foreach (var ch in _channels) ch.Writer.TryComplete();
        }

        public Task Completion => Task.WhenAll(_workers);

        private async Task RunPartitionAsync(int p)
        {
            var reader = _channels[p].Reader;
            var inflight = _inflight[p];
            var maxBatch = _strategy.MaxBatch;
            var maxWait = _strategy.MaxWait;

            while (await reader.WaitToReadAsync(_appStop).ConfigureAwait(false))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Drain immediately available items
                while (inflight.Count < maxBatch && reader.TryRead(out var item))
                    EnqueueOrCoalesce(inflight, item);

                // Timed wait to reach either MaxBatch or MaxWait
                while (inflight.Count < maxBatch && sw.Elapsed < maxWait)
                {
                    if (!await reader.WaitToReadAsync(_appStop).ConfigureAwait(false)) break;
                    while (inflight.Count < maxBatch && reader.TryRead(out var item))
                        EnqueueOrCoalesce(inflight, item);
                }

                if (inflight.Count == 0) continue; // nothing to flush

                // Flush: copy to array then clear
                var arr = ArrayPool<TItem>.Shared.Rent(inflight.Count);
                int i = 0; foreach (var kv in inflight) arr[i++] = kv.Value;
                inflight.Clear();

                try
                {
                    var batch = arr.AsSpan(0, i).ToArray();
                    await _strategy.PersistAsync(batch, _appStop).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<TItem>.Shared.Return(arr, clearArray: true);
                }
            }

            // tail flush
            if (_inflight[p].Count > 0)
                await _strategy.PersistAsync(_inflight[p].Values.ToList(), _appStop).ConfigureAwait(false);
        }

        private void EnqueueOrCoalesce(Dictionary<string, TItem> inflight, TItem incoming)
        {
            var key = _keySelector(incoming);
            if (inflight.TryGetValue(key, out var existing))
            {
                if (_strategy.TryCoalesce(existing, incoming, out var merged))
                    inflight[key] = merged;
                else
                    inflight[key] = incoming; // fallback
            }
            else
            {
                inflight[key] = incoming;
            }
        }
    }
}
