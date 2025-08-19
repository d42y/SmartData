using SmartData.Core.Queue.Writers;
using SmartData.Models;

namespace SmartData.Core.Queue.Strategies
{
    public sealed class ChangeLogStrategy : ILaneStrategy<ChangeLogItem>
    {
        private readonly ChangeLogEfBatchWriter _writer;
        public ChangeLogStrategy(ChangeLogEfBatchWriter writer) => _writer = writer;

        public int MaxBatch => 4096;
        public TimeSpan MaxWait => TimeSpan.FromSeconds(10);

        public bool TryCoalesce(ChangeLogItem a, ChangeLogItem b, out ChangeLogItem merged)
        {
            // Same entity; keep earliest original, last new per property
            var map = a.Changes.ToDictionary(x => x.Property, StringComparer.OrdinalIgnoreCase);
            foreach (var c in b.Changes)
            {
                if (map.TryGetValue(c.Property, out var ex))
                    map[c.Property] = ex with { New = c.New, ChangeType = c.ChangeType };
                else
                    map[c.Property] = c;
            }
            merged = a with { Utc = b.Utc, Changes = map.Values.ToList() };
            return true;
        }

        public async Task PersistAsync(IReadOnlyList<ChangeLogItem> batch, CancellationToken ct)
        {
            // expand to rows and write
            var rows = new List<ChangeLogRecord>(capacity: batch.Sum(b => b.Changes.Count));
            foreach (var i in batch)
            {
                foreach (var c in i.Changes)
                {
                    rows.Add(new ChangeLogRecord
                    {
                        Id = Guid.NewGuid(),
                        TableName = i.Table,
                        EntityId = i.EntityKey,
                        ChangedBy = i.Actor,
                        PropertyName = c.Property,
                        ChangedAt = i.Utc,
                        OriginalValue = c.Original,
                        NewValue = c.New,
                        ChangeType = c.ChangeType
                    });
                }
            }
            await _writer.WriteAsync(rows, ct);
        }
    }
}
