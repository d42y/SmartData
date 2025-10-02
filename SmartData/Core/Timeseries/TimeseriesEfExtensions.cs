using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SmartData.Core.Helpers;
using SmartData.Core.Queue;
using SmartData.Core.Timeseries;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.Core.Timeseries
{
    public static class TimeseriesEfExtensions
    {
        // --------------------------------------------------------------------
        // Queue-aware overloads (recommended)
        // --------------------------------------------------------------------

        // Add a single point by entity instance
        public static Task AddTimeseriesAsync<T>(
            this DbSet<T> set,
            ITimeseriesIngestQueue queue,
            T entity,
            string property,
            string value,
            DateTime? timestampUtc = null,
            CancellationToken ct = default)
            where T : class
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));
            if (string.IsNullOrWhiteSpace(property)) throw new ArgumentException("Property is required.", nameof(property));
            if (queue is null) throw new ArgumentNullException(nameof(queue));

            var ctx = set.GetService<ICurrentDbContext>().Context;
            var (table, entityId) = ResolveTableAndEntityId(ctx, entity);

            var point = new IngestPoint(
                TableName: table,
                EntityId: entityId,
                PropertyName: property,
                Value: value,
                TimestampUtc: timestampUtc ?? DateTime.UtcNow
            );

            return queue.EnqueuePointAsync(point, ct).AsTask();
        }

        // Add a single point by explicit key
        public static Task AddTimeseriesAsync<T>(
            this DbSet<T> set,
            ITimeseriesIngestQueue queue,
            string entityId,
            string property,
            string value,
            DateTime? timestampUtc = null,
            CancellationToken ct = default)
            where T : class
        {
            if (string.IsNullOrWhiteSpace(entityId)) throw new ArgumentException("entityId is required.", nameof(entityId));
            if (string.IsNullOrWhiteSpace(property)) throw new ArgumentException("Property is required.", nameof(property));
            if (queue is null) throw new ArgumentNullException(nameof(queue));

            var ctx = set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));

            var point = new IngestPoint(
                TableName: table,
                EntityId: entityId,
                PropertyName: property,
                Value: value,
                TimestampUtc: timestampUtc ?? DateTime.UtcNow
            );

            return queue.EnqueuePointAsync(point, ct).AsTask();
        }

        // Add a batch (enqueue ONE grouped item with all timestamps)
        public static async Task AddTimeseriesBatchAsync<T>(
            this DbSet<T> set,
            ITimeseriesIngestQueue queue,
            string entityId,
            string property,
            IReadOnlyList<(string Value, DateTime TimestampUtc)> points,
            CancellationToken ct = default)
            where T : class
        {
            if (string.IsNullOrWhiteSpace(entityId)) throw new ArgumentException("entityId is required.", nameof(entityId));
            if (string.IsNullOrWhiteSpace(property)) throw new ArgumentException("Property is required.", nameof(property));
            if (queue is null) throw new ArgumentNullException(nameof(queue));
            if (points is null || points.Count == 0) throw new ArgumentException("points required", nameof(points));

            var ctx = (DbContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));

            var ordered = points.OrderBy(p => p.TimestampUtc).ToList();

            var item = new TimeseriesItem(
                Table: table,
                EntityKey: entityId,
                Utc: ordered[0].TimestampUtc, // earliest in the batch
                Actor: null,
                Points: ordered
                    .Select(p => new TimeseriesPoint(property, p.Value, p.TimestampUtc))
                    .ToList());

            await queue.EnqueueGroupAsync(item, ct);
        }

        // --------------------------------------------------------------------
        // Disabled legacy overloads (double) — keeps callers from using old API
        // --------------------------------------------------------------------
        [Obsolete("Inject ITimeseriesIngestQueue and use the string-value overloads.")]
        public static Task AddTimeseriesAsync<T>(
            this DbSet<T> set,
            T entity,
            string property,
            double value,
            DateTime? timestampUtc = null,
            CancellationToken ct = default)
            where T : class =>
            throw new InvalidOperationException(
                "Timeseries overload without ITimeseriesIngestQueue is disabled. " +
                "Inject ITimeseriesIngestQueue and use the queue-aware overloads (string Value).");

        [Obsolete("Inject ITimeseriesIngestQueue and use the string-value overloads.")]
        public static Task AddTimeseriesAsync<T>(
            this DbSet<T> set,
            string entityId,
            string property,
            double value,
            DateTime? timestampUtc = null,
            CancellationToken ct = default)
            where T : class =>
            throw new InvalidOperationException(
                "Timeseries overload without ITimeseriesIngestQueue is disabled. " +
                "Inject ITimeseriesIngestQueue and use the queue-aware overloads (string Value).");

        // --------------------------------------------------------------------
        // Reads (raw + interpolated)
        // --------------------------------------------------------------------
        public static Task<List<TimeseriesResult>> GetTimeseriesAsync<T>(
            this DbSet<T> set,
            string entityId,
            string property,
            DateTime startUtc,
            DateTime endUtc,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            return ctx.GetTimeseriesAsync(table, entityId, property, startUtc, endUtc, ct);
        }

        public static Task<List<TimeseriesResult>> GetTimeseriesAsync<T>(
            this DbSet<T> set,
            string entityId,
            string property,
            DateTime startUtc,
            DateTime endUtc,
            TimeSpan interval,
            InterpolationMethod method,
            CancellationToken ct = default)
            where T : class
        {
            var ctx = (DataContext)set.GetService<ICurrentDbContext>().Context;
            var table = TableHelper.ResolveTableName(ctx.Model, typeof(T));
            return ctx.GetInterpolatedTimeseriesAsync(table, entityId, property, startUtc, endUtc, interval, method, ct);
        }

        // --------------------------------------------------------------------
        // Helpers
        // --------------------------------------------------------------------
        private static (string TableName, string EntityId) ResolveTableAndEntityId(DbContext dbContext, object entity)
        {
            var model = dbContext.Model;
            var et = model.FindEntityType(entity.GetType())
                     ?? throw new InvalidOperationException($"Unknown entity type {entity.GetType().Name} in model.");

            var table = et.GetTableName()
                       ?? throw new InvalidOperationException($"No table name for entity type {et.Name}.");

            var pk = et.FindPrimaryKey()
                   ?? throw new InvalidOperationException($"Entity type {et.Name} has no primary key.");

            var values = pk.Properties
                .Select(p =>
                {
                    var clrProp = entity.GetType().GetProperty(p.Name);
                    var v = clrProp?.GetValue(entity);
                    if (v is null) throw new InvalidOperationException($"Primary key property {p.Name} is null.");
                    return v.ToString();
                })
                .ToArray();

            var entityId = string.Join("|", values);
            return (table, entityId);
        }
    }
}
