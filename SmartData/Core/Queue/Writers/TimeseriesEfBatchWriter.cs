using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.Core.Queue.Writers
{
    /// <summary>
    /// Batch writer for timeseries. Upserts base values and appends
    /// deltas. Uses the existing TimeseriesDelta.AddTimestamp(int) API.
    /// </summary>
    public sealed class TimeseriesEfBatchWriter
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<TimeseriesEfBatchWriter> _log;

        public TimeseriesEfBatchWriter(IServiceProvider sp, ILogger<TimeseriesEfBatchWriter> log)
        {
            _sp = sp; _log = log;
        }

        public async Task WriteAsync(
            IReadOnlyList<(string Table, string EntityId, string Property, string Value, DateTime BaseTimestamp, IReadOnlyList<DateTime> Deltas)> rows,
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
                // Upsert base values (simple in-memory upsert via one fetch)
                var baseKeys = rows
                    .Select(r => (r.Table, r.EntityId, r.Property, r.Value))
                    .Distinct()
                    .ToList();

                var existing = await db.Set<TimeseriesBaseValue>()
                    .Where(b => baseKeys.Select(k => k.Table).Contains(b.TableName)
                             && baseKeys.Select(k => k.EntityId).Contains(b.EntityId)
                             && baseKeys.Select(k => k.Property).Contains(b.PropertyName)
                             && baseKeys.Select(k => k.Value).Contains(b.Value))
                    .ToListAsync(ct);

                var baseMap = new Dictionary<(string Table, string Entity, string Prop, string Value), TimeseriesBaseValue>(
                    capacity: existing.Count);

                foreach (var b in existing)
                    baseMap[(b.TableName, b.EntityId, b.PropertyName, b.Value)] = b;

                var toInsert = new List<TimeseriesBaseValue>();
                foreach (var bk in baseKeys)
                {
                    if (!baseMap.TryGetValue((bk.Table, bk.EntityId, bk.Property, bk.Value), out var bv))
                    {
                        bv = new TimeseriesBaseValue
                        {
                            Id = Guid.NewGuid(),
                            TableName = bk.Table,
                            EntityId = bk.EntityId,
                            PropertyName = bk.Property,
                            Value = bk.Value,
                            Timestamp = DateTime.UtcNow
                        };
                        baseMap[(bk.Table, bk.EntityId, bk.Property, bk.Value)] = bv;
                        toInsert.Add(bv);
                    }
                }

                if (toInsert.Count > 0)
                    await db.Set<TimeseriesBaseValue>().AddRangeAsync(toInsert, ct);

                // Build deltas. Your current API uses AddTimestamp(int), so we’ll
                // keep the same (0) marker just like your original code.
                var deltas = new List<TimeseriesDelta>(rows.Count);
                foreach (var r in rows)
                {
                    var baseId = baseMap[(r.Table, r.EntityId, r.Property, r.Value)].Id;
                    var delta = new TimeseriesDelta
                    {
                        Id = Guid.NewGuid(),
                        BaseValueId = baseId,
                        Version = 1
                    };

                    // Preserve your prior behavior: push a marker per incoming point
                    // but use 0 so we don't call a non-existent DateTime overload.
                    var count = r.Deltas?.Count ?? 0;
                    for (int i = 0; i < count; i++)
                        delta.AddTimestamp(0);

                    deltas.Add(delta);
                }

                if (deltas.Count > 0)
                    await db.Set<TimeseriesDelta>().AddRangeAsync(deltas, ct);

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Timeseries batch failed ({Count})", rows.Count);
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
