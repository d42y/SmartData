using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SmartData.Core.Queue;
using SmartData.Models;

namespace SmartData.Core.Timeseries
{
    public interface ITimeseriesIngestQueue
    {
        // Single point (wrapped into a one-item group)
        ValueTask EnqueuePointAsync(IngestPoint point, CancellationToken ct = default);

        // Whole group (preferred for batch endpoints)
        ValueTask EnqueueGroupAsync(TimeseriesItem group, CancellationToken ct = default);

        // Convenience: take many points and send as ONE grouped item
        ValueTask EnqueuePointsAsync(IEnumerable<IngestPoint> points, CancellationToken ct = default);

        // Reader yields grouped items
        IAsyncEnumerable<TimeseriesItem> ReadAllAsync(CancellationToken ct);
    }

    internal sealed class TimeseriesIngestQueue : ITimeseriesIngestQueue
    {
        private readonly Channel<TimeseriesItem> _channel;

        public TimeseriesIngestQueue(TimeseriesIngestOptions options)
        {
            var cap = Math.Max(10_000, options.ChannelCapacity);
            var bounded = new BoundedChannelOptions(cap)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            };
            _channel = Channel.CreateBounded<TimeseriesItem>(bounded);
        }

        public ValueTask EnqueuePointAsync(IngestPoint p, CancellationToken ct = default)
        {
            var item = new TimeseriesItem(
                Table: p.TableName,
                EntityKey: p.EntityId,
                Utc: p.TimestampUtc,     // anchor to the point time
                Actor: null,
                Points: new List<TimeseriesPoint> {
                    new TimeseriesPoint(
                        Property: p.PropertyName,
                        Value:    p.Value,        // NOTE: double, not string
                        TimestampUtc: p.TimestampUtc)
                });
            return _channel.Writer.WriteAsync(item, ct);
        }

        public ValueTask EnqueueGroupAsync(TimeseriesItem group, CancellationToken ct = default)
        {
            // normalize & order; set Utc = earliest point
            var points = group.Points
                .Where(p => p is not null)
                .OrderBy(p => p.TimestampUtc)
                .Select(p => new TimeseriesPoint(
                    Property: p.Property?.Trim() ?? string.Empty,
                    Value: p.Value,             // keep as double
                    TimestampUtc: p.TimestampUtc))
                .ToList();

            var utc = points.Count > 0 ? points[0].TimestampUtc : group.Utc;

            var normalized = new TimeseriesItem(
                Table: group.Table,
                EntityKey: group.EntityKey,
                Utc: utc,
                Actor: group.Actor,
                Points: points);

            return _channel.Writer.WriteAsync(normalized, ct);
        }

        public ValueTask EnqueuePointsAsync(IEnumerable<IngestPoint> points, CancellationToken ct = default)
        {
            if (points is null) throw new ArgumentNullException(nameof(points));
            var list = points.ToList();
            if (list.Count == 0) return ValueTask.CompletedTask;

            var first = list[0];
            var pts = list
                .OrderBy(p => p.TimestampUtc)
                .Select(p => new TimeseriesPoint(
                    Property: p.PropertyName,
                    Value: p.Value,              // double
                    TimestampUtc: p.TimestampUtc))
                .ToList();

            var item = new TimeseriesItem(
                Table: first.TableName,
                EntityKey: first.EntityId,
                Utc: pts[0].TimestampUtc,          // earliest
                Actor: null,
                Points: pts);

            return _channel.Writer.WriteAsync(item, ct);
        }

        public IAsyncEnumerable<TimeseriesItem> ReadAllAsync(CancellationToken ct)
            => _channel.Reader.ReadAllAsync(ct);
    }
}
