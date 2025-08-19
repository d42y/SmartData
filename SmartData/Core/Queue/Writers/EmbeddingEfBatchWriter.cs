using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Data;
using SmartData.Models;
using SmartData.Vectorizer;

namespace SmartData.Core.Queue.Writers
{
    public sealed class EmbeddingEfBatchWriter
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<EmbeddingEfBatchWriter> _log;
        private readonly IEmbeddingProvider? _embedder;
        private readonly IFaissSearch? _faiss;

        public EmbeddingEfBatchWriter(
            IServiceProvider sp,
            ILogger<EmbeddingEfBatchWriter> log,
            IEmbeddingProvider? embedder,
            IFaissSearch? faiss)
        {
            _sp = sp;
            _log = log;
            _embedder = embedder;
            _faiss = faiss;
        }

        public async Task WriteAsync(
            IReadOnlyList<(EmbeddingItem Item, string Paragraph)> items,
            CancellationToken ct)
        {
            if (items.Count == 0 || _embedder == null || _faiss == null) return;

            // Compute vectors (batch if your provider supports it)
            var vectors = new List<(Guid Id, string Table, string EntityId, float[] Vec)>(items.Count);
            foreach (var (it, text) in items)
            {
                var vec = _embedder.GenerateEmbedding(text);
                // new id only used when INSERTing a new record
                vectors.Add((Guid.NewGuid(), it.Table, it.EntityKey, vec));
            }

            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();

            var prevAuto = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Load existing rows for the (Table, EntityId) pairs in this batch
                var tSet = vectors.Select(v => v.Table).ToHashSet(StringComparer.Ordinal);
                var eSet = vectors.Select(v => v.EntityId).ToHashSet(StringComparer.Ordinal);

                var existing = await db.Set<EmbeddingRecord>()
                    .Where(r => tSet.Contains(r.TableName) && eSet.Contains(r.EntityId))
                    .ToListAsync(ct);

                // IMPORTANT: use a tuple comparer (or no comparer) — NOT StringComparer
                var map = existing.ToDictionary<
                    EmbeddingRecord,
                    (string Table, string EntityId),
                    EmbeddingRecord>(
                    r => (r.TableName, r.EntityId),
                    r => r);
                // Alternative (also fine):
                // var map = new Dictionary<(string Table, string EntityId), EmbeddingRecord>();
                // foreach (var r in existing) map[(r.TableName, r.EntityId)] = r;

                // Upsert DB + update FAISS
                var table = db.Set<EmbeddingRecord>();

                foreach (var v in vectors)
                {
                    if (!map.TryGetValue((v.Table, v.EntityId), out var rec))
                    {
                        rec = new EmbeddingRecord
                        {
                            Id = v.Id,
                            TableName = v.Table,
                            EntityId = v.EntityId,
                            Embedding = v.Vec
                        };
                        await table.AddAsync(rec, ct);
                        _faiss.AddEmbedding(rec.Id, v.Vec);
                    }
                    else
                    {
                        rec.Embedding = v.Vec;
                        db.Update(rec);
                        _faiss.UpdateEmbedding(rec.Id, v.Vec);
                    }
                }

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Embedding batch failed ({Count})", items.Count);
                await tx.RollbackAsync(ct);
                throw;
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = prevAuto;
            }
        }
    }
}
