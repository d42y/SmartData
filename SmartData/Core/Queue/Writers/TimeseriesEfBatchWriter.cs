using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.Core.Queue.Writers
{
    public sealed class TimeseriesEfBatchWriter
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<TimeseriesEfBatchWriter> _log;

        public TimeseriesEfBatchWriter(IServiceProvider sp, ILogger<TimeseriesEfBatchWriter> log)
        {
            _sp = sp; _log = log;
        }

        public async Task WriteAsync(
            IReadOnlyList<(string Table, string EntityId, string Property, string Value, DateTime BaseTs, IReadOnlyList<DateTime> Deltas)> rows,
            CancellationToken ct)
        {
            if (rows is null || rows.Count == 0) return;

            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();

            var prevAuto = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Coalesce to exactly one group per (T,E,P,V) for this batch
                var groups = rows
                    .GroupBy(r => new { r.Table, r.EntityId, r.Property, r.Value })
                    .Select(g =>
                    {
                        var baseTs = g.Min(x => x.BaseTs);
                        var all = g.SelectMany(x => x.Deltas)
                                   .Append(baseTs)
                                   .Distinct()
                                   .OrderBy(t => t)
                                   .ToList();
                        return (
                            Table: g.Key.Table,
                            EntityId: g.Key.EntityId,
                            Property: g.Key.Property,
                            Value: g.Key.Value,
                            BaseTs: all[0],
                            AllTs: (IReadOnlyList<DateTime>)all
                        );
                    })
                    .ToList();

                foreach (var g in groups)
                {
                    // 1) ALWAYS create a new base anchored at this batch's earliest timestamp
                    var baseRow = new TimeseriesBaseValue
                    {
                        Id = Guid.NewGuid(),
                        TableName = g.Table,
                        EntityId = g.EntityId,
                        PropertyName = g.Property,
                        Value = g.Value,
                        Timestamp = g.BaseTs
                    };

                    db.Entry(baseRow).State = EntityState.Added;
                    await db.SaveChangesAsync(ct);

                    // 2) Build offsets relative to THIS base we just wrote
                    static int ToSec(DateTime ts, DateTime baseTs)
                        => (int)Math.Round((ts - baseTs).TotalSeconds, MidpointRounding.AwayFromZero);

                    var offsets = g.AllTs
                        .Where(ts => ts > g.BaseTs)
                        .Select(ts => ToSec(ts, g.BaseTs))
                        .OrderBy(off => off)
                        .ToList();

                    if (offsets.Count == 0)
                    {
                        _log.LogInformation("TS Writer: base {BaseId} created with no deltas (single point).", baseRow.Id);
                        continue;
                    }

                    var payload = PackOffsets(offsets);

                    var delta = new TimeseriesDelta
                    {
                        Id = Guid.NewGuid(),
                        BaseValueId = baseRow.Id,
                        Version = 1,
                        Deltas = payload,       // Ensure column is VARBINARY(MAX)
                        LastTimestamp = offsets[^1]    // seconds since base
                    };

                    db.Entry(delta).State = EntityState.Added;
                    await db.SaveChangesAsync(ct);

                    _log.LogInformation("TS Writer: base {BaseId} @ {BaseTs:o} -> delta {DeltaId} count={Count} bytes={Bytes} lastOff={Last}",
                        baseRow.Id, g.BaseTs, delta.Id, offsets.Count, payload.Length, delta.LastTimestamp);
                }

                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Timeseries batch failed (rows={Count})", rows?.Count ?? 0);
                await tx.RollbackAsync(ct);
                throw;
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = prevAuto;
            }
        }

        // 4-byte little-endian ints
        private static byte[] PackOffsets(IReadOnlyList<int> offsets)
        {
            var buf = new byte[offsets.Count * 4];
            for (int i = 0; i < offsets.Count; i++)
                BitConverter.TryWriteBytes(new Span<byte>(buf, i * 4, 4), offsets[i]);
            return buf;
        }
    }
}
