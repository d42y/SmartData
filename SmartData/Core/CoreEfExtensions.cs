using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SmartData.Core.Helpers;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.Core
{
    public static class CoreEfExtensions
    {
        // -------------------- CHANGE LOG --------------------

        /// <summary>
        /// Paged change-log rows for a single entity/property within a time window.
        /// </summary>
        public static Task<ChangeLogPage> GetChangesAsync<T>(
            this DbSet<T> set,
            string entityId,
            string property,
            DateTime startUtc,
            DateTime endUtc,
            string? actor = null,
            string? changeType = null,
            int skip = 0,
            int take = 200,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            var q = new ChangeLogQuery(table, entityId, startUtc, endUtc, property, actor, changeType, skip, take);
            return ctx.GetChangeLogAsync(q, ct);
        }

        /// <summary>
        /// Aggregate change summary for an entity (optionally scoped by property/actor/type/time).
        /// </summary>
        public static Task<ChangeSummary> GetChangeSummaryAsync<T>(
            this DbSet<T> set,
            string entityId,
            DateTime? fromUtc = null,
            DateTime? toUtc = null,
            string? actor = null,
            string? propertyName = null,
            string? changeType = null,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            return ctx.GetChangeSummaryAsync(table, entityId, fromUtc, toUtc, actor, propertyName, changeType, ct);
        }

        /// <summary>
        /// Most recent change timestamp for a table/entity/property (null if none).
        /// </summary>
        public static Task<DateTime?> GetLastChangeUtcAsync<T>(
            this DbSet<T> set,
            string entityId,
            string? propertyName = null,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            return ctx.GetLastChangeUtcAsync(table, entityId, propertyName, ct);
        }

        // -------------------- INTEGRITY --------------------

        /// <summary>
        /// Verify hash chain for a single property’s integrity log.
        /// Returns true if valid or no chain info is present.
        /// </summary>
        public static Task<bool> VerifyIntegrityChainAsync<T>(
            this DbSet<T> set,
            string entityId,
            string propertyName,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            return ctx.VerifyIntegrityChainAsync(table, entityId, propertyName, ct);
        }

        /// <summary>
        /// Compare a current value (already normalized/serialized as string) to the latest logged hash.
        /// </summary>
        public static Task<bool> VerifyIntegrityLatestAsync<T>(
            this DbSet<T> set,
            string entityId,
            string propertyName,
            string currentValue,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            return ctx.VerifyIntegrityLatestAsync(table, entityId, propertyName, currentValue, ct);
        }

        /// <summary>
        /// Convenience overload: pass a numeric value; we stringify using invariant culture.
        /// </summary>
        public static Task<bool> VerifyIntegrityLatestAsync<T>(
            this DbSet<T> set,
            string entityId,
            string propertyName,
            double currentValue,
            CancellationToken ct = default)
            where T : class
        {
            var s = currentValue.ToString(CultureInfo.InvariantCulture);
            return set.VerifyIntegrityLatestAsync(entityId, propertyName, s, ct);
        }

        /// <summary>
        /// Load the latest integrity snapshot (latest hash/timestamp per property) for an entity.
        /// </summary>
        public static Task<Dictionary<string, (string Hash, DateTime Utc)>> GetLatestIntegritySnapshotAsync<T>(
            this DbSet<T> set,
            string entityId,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            return ctx.GetLatestIntegritySnapshotAsync(table, entityId, ct);
        }

        /// <summary>
        /// Check integrity for the given set of current values (key = property name).
        /// Returns per-property match results and timestamps.
        /// </summary>
        public static Task<IntegrityCheckResult> CheckIntegrityAsync<T>(
            this DbSet<T> set,
            string entityId,
            IReadOnlyDictionary<string, object?> currentValues,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            return ctx.CheckIntegrityAsync(table, entityId, currentValues, ct);
        }
    }
}
