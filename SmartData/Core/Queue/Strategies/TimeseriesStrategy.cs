using SmartData.Core.Queue.Writers;
using SmartData.Models;

namespace SmartData.Core.Queue.Strategies
{
    public sealed class TimeseriesStrategy : ILaneStrategy<TimeseriesItem>
    {
        private readonly TimeseriesEfBatchWriter _writer;
        public TimeseriesStrategy(TimeseriesEfBatchWriter writer) => _writer = writer;

        public int MaxBatch => 2048;
        public TimeSpan MaxWait => TimeSpan.FromMilliseconds(500);

        public bool TryCoalesce(TimeseriesItem a, TimeseriesItem b, out TimeseriesItem merged)
        {
            // combine points; if same (prop,value,timestamp) appears, keep newest timestamp list
            var list = new List<TimeseriesPoint>(a.Points.Count + b.Points.Count);
            list.AddRange(a.Points); list.AddRange(b.Points);
            merged = a with { Utc = b.Utc, Points = list };
            return true;
        }

        public async Task PersistAsync(IReadOnlyList<TimeseriesItem> batch, CancellationToken ct)
        {
            // Group by base (Table, Entity, Prop, Value), collect timestamps
            var grouped = batch
                .SelectMany(i => i.Points.Select(p => (i.Table, i.EntityKey, p.Property, p.Value, p.TimestampUtc)))
                .GroupBy(k => (k.Table, k.EntityKey, k.Property, k.Value))
                .Select(g => (
                    g.Key.Table, g.Key.EntityKey, g.Key.Property, g.Key.Value,
                    BaseTs: DateTime.UtcNow, // or min(g.Select(x=>x.TimestampUtc))
                    Deltas: (IReadOnlyList<DateTime>)g.Select(x => x.TimestampUtc).OrderBy(x => x).ToList()))
                .ToList();

            await _writer.WriteAsync(grouped, ct);
        }
    }
}
