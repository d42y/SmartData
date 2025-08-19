using SmartData.Core.Queue.Writers;

namespace SmartData.Core.Queue.Strategies
{
    public sealed class IntegrityStrategy : ILaneStrategy<IntegrityItem>
    {
        private readonly IntegrityEfBatchWriter _writer;
        public IntegrityStrategy(IntegrityEfBatchWriter writer) => _writer = writer;

        public int MaxBatch => 2048;
        public TimeSpan MaxWait => TimeSpan.FromMilliseconds(250);

        public bool TryCoalesce(IntegrityItem a, IntegrityItem b, out IntegrityItem merged)
        {
            var map = a.Props.ToDictionary(p => p.Property, StringComparer.OrdinalIgnoreCase);
            foreach (var p in b.Props) map[p.Property] = p; // last hash wins
            merged = a with { Utc = b.Utc, Props = map.Values.ToList() };
            return true;
        }

        public async Task PersistAsync(IReadOnlyList<IntegrityItem> batch, CancellationToken ct)
        {
            static string Sha256(string s)
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty)));
            }

            var rows = new List<(string Table, string EntityId, string Property, string Hash, DateTime Utc)>(
                batch.Sum(b => b.Props.Count));

            foreach (var i in batch)
                foreach (var p in i.Props)
                    rows.Add((i.Table, i.EntityKey, p.Property, Sha256(p.CurrentValue), i.Utc));

            await _writer.WriteAsync(rows, ct);
        }
    }
}
