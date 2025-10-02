using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartData.Core.Queue;
using SmartData.Core.Queue.Writers;
using SmartData.Models;

namespace SmartData.Core.Timeseries
{
    public sealed class TimeseriesIngestWorker : BackgroundService
    {
        private readonly IServiceProvider _sp;
        private readonly ITimeseriesIngestQueue _queue;
        private readonly TimeseriesIngestOptions _options;
        private readonly ILogger<TimeseriesIngestWorker> _log;

        public TimeseriesIngestWorker(
            IServiceProvider sp,
            ITimeseriesIngestQueue queue,
            TimeseriesIngestOptions options,
            ILogger<TimeseriesIngestWorker> log)
        {
            _sp = sp;
            _queue = queue;
            _options = options;
            _log = log;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var dop = Math.Max(1, _options.DegreeOfParallelism);
            var workers = Enumerable.Range(0, dop)
                .Select(_ => Task.Run(() => WorkerLoop(stoppingToken), stoppingToken));
            return Task.WhenAll(workers);
        }

        private async Task WorkerLoop(CancellationToken ct)
        {
            // local buffer holds grouped items (NOT individual points)
            var buffer = new List<TimeseriesItem>(_options.MaxBatchSize);
            var nextFlushAt = DateTime.UtcNow + _options.MaxWait;

            await foreach (var groupedItem in _queue.ReadAllAsync(ct))
            {
                buffer.Add(groupedItem);

                var timeToFlush = DateTime.UtcNow >= nextFlushAt;
                var sizeToFlush = buffer.Count >= _options.MaxBatchSize;

                if (timeToFlush || sizeToFlush)
                {
                    await FlushAsync(buffer, ct);
                    buffer.Clear();
                    nextFlushAt = DateTime.UtcNow + _options.MaxWait;
                }
            }

            // drain on shutdown
            if (buffer.Count > 0)
            {
                await FlushAsync(buffer, ct);
                buffer.Clear();
            }
        }

        private async Task FlushAsync(List<TimeseriesItem> items, CancellationToken ct)
        {
            if (items.Count == 0) return;

            try
            {
                using var scope = _sp.CreateScope();
                var writer = scope.ServiceProvider.GetRequiredService<TimeseriesEfBatchWriter>();

                // Flatten all points from all grouped items, normalize, then coalesce by (T,E,Prop,Value)
                var grouped = items
                    .SelectMany(g => g.Points.Select(p => new
                    {
                        g.Table,
                        g.EntityKey,
                        p.Property,
                        ValueKey = p.Value,
                        p.TimestampUtc
                    }))
                    .GroupBy(x => new { x.Table, x.EntityKey, x.Property, x.ValueKey })
                    .Select(g =>
                    {
                        var ordered = g.Select(x => x.TimestampUtc)
                                       .Distinct()
                                       .OrderBy(t => t)
                                       .ToList();

                        return (
                            Table: g.Key.Table,
                            EntityId: g.Key.EntityKey,
                            Property: g.Key.Property,
                            Value: g.Key.ValueKey,                 // string key used by the writer
                            BaseTs: ordered[0],                    // earliest in the coalesced group
                            Deltas: (IReadOnlyList<DateTime>)ordered
                        );
                    })
                    .ToList();

                if (grouped.Count == 0) return;

                await writer.WriteAsync(grouped, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Timeseries flush failed for {Count} grouped items", items.Count);
                // swallow to keep the loop alive
            }
        }
    }
}
