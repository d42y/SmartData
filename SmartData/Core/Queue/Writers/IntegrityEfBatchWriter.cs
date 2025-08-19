using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.Core.Queue.Writers
{
    /// <summary>
    /// Batch writer for integrity logs that computes PreviousHash by
    /// fetching the latest IntegrityLogRecord per (Table, Entity, Property)
    /// in a single set-based query. No IntegrityCurrent table required.
    /// </summary>
    public sealed class IntegrityEfBatchWriter
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<IntegrityEfBatchWriter> _log;

        public IntegrityEfBatchWriter(IServiceProvider sp, ILogger<IntegrityEfBatchWriter> log)
        {
            _sp = sp; _log = log;
        }

        public async Task WriteAsync(
            IReadOnlyList<(string Table, string EntityId, string Property, string Hash, DateTime Utc)> rows,
            CancellationToken ct)
        {
            if (rows.Count == 0) return;

            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();

            var prevAuto = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Distinct keys in this batch
                var keys = rows
                    .Select(r => new { r.Table, r.EntityId, r.Property })
                    .Distinct()
                    .ToList();

                // Pull the latest prior log row for each key in one go
                var latestPrior = await db.Set<IntegrityLogRecord>()
                    .Where(l => keys.Select(k => k.Table).Contains(l.TableName)
                             && keys.Select(k => k.EntityId).Contains(l.EntityId)
                             && keys.Select(k => k.Property).Contains(l.PropertyName))
                    .GroupBy(l => new { l.TableName, l.EntityId, l.PropertyName })
                    .Select(g => g.OrderByDescending(x => x.Timestamp).FirstOrDefault())
                    .ToListAsync(ct);

                // Build a lookup: (Table, Entity, Property) -> last Hash
                var lastMap = new Dictionary<(string Table, string Entity, string Prop), string>(
                    capacity: latestPrior.Count);

                foreach (var lp in latestPrior)
                {
                    if (lp is null) continue;
                    lastMap[(lp.TableName, lp.EntityId, lp.PropertyName)] = lp.Hash ?? string.Empty;
                }

                // Create new log rows with computed PreviousHash
                var logs = new List<IntegrityLogRecord>(rows.Count);
                foreach (var r in rows)
                {
                    lastMap.TryGetValue((r.Table, r.EntityId, r.Property), out var prevHash);

                    logs.Add(new IntegrityLogRecord
                    {
                        Id = Guid.NewGuid(),
                        TableName = r.Table,
                        EntityId = r.EntityId,
                        PropertyName = r.Property,
                        Hash = r.Hash,
                        PreviousHash = prevHash ?? string.Empty,
                        Timestamp = r.Utc
                    });

                    // Update the lastMap so multiple entries for the same key
                    // in this *same* batch chain correctly.
                    lastMap[(r.Table, r.EntityId, r.Property)] = r.Hash;
                }

                await db.Set<IntegrityLogRecord>().AddRangeAsync(logs, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Integrity batch failed ({Count})", rows.Count);
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
