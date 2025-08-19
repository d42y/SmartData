using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartData.Core.Queue.Writers;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.Core.Queue.Strategies
{
    public sealed class EmbeddingStrategy : ILaneStrategy<EmbeddingItem>
    {
        private readonly EmbeddingEfBatchWriter _writer;
        private readonly IServiceProvider _sp;

        public EmbeddingStrategy(EmbeddingEfBatchWriter writer, IServiceProvider sp)
        { _writer = writer; _sp = sp; }

        public int MaxBatch => 256;
        public TimeSpan MaxWait => TimeSpan.FromMilliseconds(500);

        public bool TryCoalesce(EmbeddingItem a, EmbeddingItem b, out EmbeddingItem merged)
        {
            // last-writer-wins; only the latest entity state matters for embedding
            merged = b with { NeedEmbedding = a.NeedEmbedding || b.NeedEmbedding };
            return true;
        }

        public async Task PersistAsync(IReadOnlyList<EmbeddingItem> batch, CancellationToken ct)
        {
            if (batch.Count == 0) return;

            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            // Load entities and build paragraph text per Embeddable attributes
            var parser = new SmartData.Vectorizer.EmbeddingExpressionParser(db);
            var outputs = new List<(EmbeddingItem Item, string Paragraph)>(batch.Count);

            foreach (var it in batch)
            {
                if (!it.NeedEmbedding) continue;
                var entity = await db.FindAsync(it.EntityType, it.KeyValues, ct);
                if (entity is null) continue;

                var props = it.EntityType.GetProperties()
                    .Select(p => (Prop: p, Attr: p.GetCustomAttributes(typeof(EmbeddableAttribute), true).FirstOrDefault()))
                    .Where(x => x.Attr != null)
                    .Select(x => (x.Prop, (EmbeddableAttribute)x.Attr!))
                    .OrderBy(x => x.Item2.Priority)
                    .ToList();

                var sb = new System.Text.StringBuilder();
                foreach (var (p, a) in props)
                {
                    var text = parser.EvaluateExpression(entity, it.EntityType, a.Format);
                    if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append(' ');
                }
                var meta = it.EntityType.GetCustomAttributes(typeof(EmbeddableAttribute), true)
                    .Cast<EmbeddableAttribute>().FirstOrDefault();
                if (meta != null)
                {
                    var text = parser.EvaluateExpression(entity, it.EntityType, meta.Format);
                    if (!string.IsNullOrWhiteSpace(text)) sb.Append(text).Append(' ');
                }
                var paragraph = sb.ToString().Trim();
                if (paragraph.Length > 0) outputs.Add((it, paragraph));
            }

            await _writer.WriteAsync(outputs, ct);
        }
    }
}
