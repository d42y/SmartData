using Microsoft.EntityFrameworkCore;
using SmartData.Models;

namespace SmartData.Data
{
    public partial class DataContext : DbContext
    {
 
        /// <summary>
        /// Return paged change-log rows for a given entity with optional filters.
        /// </summary>
        public async Task<ChangeLogPage> GetChangeLogAsync(ChangeLogQuery q, CancellationToken ct = default)
        {
            if (!_options.EnableChangeTracking)
                return new ChangeLogPage(0, Array.Empty<ChangeLogRecord>());

            IQueryable<ChangeLogRecord> query = ChangeLogRecords.AsNoTracking()
                .Where(x => x.TableName == q.TableName && x.EntityId == q.EntityId);

            if (q.FromUtc is DateTime f) query = query.Where(x => x.ChangedAt >= f);
            if (q.ToUtc is DateTime t) query = query.Where(x => x.ChangedAt <= t);
            if (!string.IsNullOrWhiteSpace(q.PropertyName))
                query = query.Where(x => x.PropertyName == q.PropertyName);
            if (!string.IsNullOrWhiteSpace(q.Actor))
                query = query.Where(x => x.ChangedBy == q.Actor);
            if (!string.IsNullOrWhiteSpace(q.ChangeType))
                query = query.Where(x => x.ChangeType == q.ChangeType);

            query = query.OrderByDescending(x => x.ChangedAt);

            var total = await query.CountAsync(ct);
            var items = await query.Skip(Math.Max(0, q.Skip))
                                   .Take(Math.Clamp(q.Take, 1, 2000))
                                   .ToListAsync(ct);

            return new ChangeLogPage(total, items);
        }

        public async Task<ChangeSummary> GetChangeSummaryAsync(
    string tableName,
    string? entityId = null,
    DateTime? fromUtc = null,
    DateTime? toUtc = null,
    string? actor = null,
    string? propertyName = null,
    string? changeType = null,
    CancellationToken ct = default)
        {
            if (!_options.EnableChangeTracking)
                return new ChangeSummary(tableName, entityId, 0, null, null,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ChangePropertySummary>());

            var q = ChangeLogRecords.AsNoTracking()
                .Where(x => x.TableName == tableName);

            if (!string.IsNullOrWhiteSpace(entityId))
                q = q.Where(x => x.EntityId == entityId);
            if (fromUtc is DateTime f)
                q = q.Where(x => x.ChangedAt >= f);
            if (toUtc is DateTime t)
                q = q.Where(x => x.ChangedAt <= t);
            if (!string.IsNullOrWhiteSpace(actor))
                q = q.Where(x => x.ChangedBy == actor);
            if (!string.IsNullOrWhiteSpace(propertyName))
                q = q.Where(x => x.PropertyName == propertyName);
            if (!string.IsNullOrWhiteSpace(changeType))
                q = q.Where(x => x.ChangeType == changeType);

            // Pull the filtered rows (entity-scoped is expected; add a time window if large)
            var rows = await q.ToListAsync(ct);

            if (rows.Count == 0)
                return new ChangeSummary(tableName, entityId, 0, null, null,
                    Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ChangePropertySummary>());

            var total = rows.Count;
            var firstUtc = rows.Min(r => r.ChangedAt);
            var lastUtc = rows.Max(r => r.ChangedAt);
            var actors = rows.Select(r => r.ChangedBy).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToList();
            var types = rows.Select(r => r.ChangeType).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToList();

            var props = rows
                .GroupBy(r => r.PropertyName ?? "(entity)")
                .Select(g =>
                {
                    var ordered = g.OrderBy(r => r.ChangedAt).ToList();
                    var first = ordered.First();
                    var last = ordered.Last();
                    return new ChangePropertySummary(
                        PropertyName: g.Key,
                        Count: g.Count(),
                        FirstUtc: first.ChangedAt,
                        LastUtc: last.ChangedAt,
                        FirstValue: first.NewValue,     // earliest observed new value
                        LastValue: last.NewValue,      // latest observed new value
                        LastChangeType: last.ChangeType,
                        LastChangedBy: last.ChangedBy
                    );
                })
                .OrderByDescending(p => p.LastUtc)
                .ToList();

            return new ChangeSummary(tableName, entityId, total, firstUtc, lastUtc, actors, types, props);
        }

        // ===================== Last change timestamp =====================
        public async Task<DateTime?> GetLastChangeUtcAsync(
            string tableName,
            string? entityId = null,
            string? propertyName = null,
            CancellationToken ct = default)
        {
            if (!_options.EnableChangeTracking) return null;

            var q = ChangeLogRecords.AsNoTracking()
                .Where(x => x.TableName == tableName);

            if (!string.IsNullOrWhiteSpace(entityId))
                q = q.Where(x => x.EntityId == entityId);
            if (!string.IsNullOrWhiteSpace(propertyName))
                q = q.Where(x => x.PropertyName == propertyName);

            // Returns null when no rows exist
            return await q.OrderByDescending(x => x.ChangedAt)
                          .Select(x => (DateTime?)x.ChangedAt)
                          .FirstOrDefaultAsync(ct);
        }

    }
}
