using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.Core.Queue.Writers
{
    // CHANGE LOG
    public sealed class ChangeLogEfBatchWriter
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<ChangeLogEfBatchWriter> _log;
        public ChangeLogEfBatchWriter(IServiceProvider sp, ILogger<ChangeLogEfBatchWriter> log) { _sp = sp; _log = log; }

        public async Task WriteAsync(IReadOnlyList<ChangeLogRecord> rows, CancellationToken ct)
        {
            if (rows.Count == 0) return;
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();

            var prev = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Set<ChangeLogRecord>().AddRangeAsync(rows, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ChangeLog batch failed ({Count})", rows.Count);
                await tx.RollbackAsync(ct);
                throw;
            }
            finally { db.ChangeTracker.AutoDetectChangesEnabled = prev; }
        }
    }
}
