// SmartData.Core/SmartDataWriteInterceptor.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using SmartData.Core.Queue;
using SmartData.Data;
using SmartData.Models;
using System.ComponentModel.DataAnnotations;

namespace SmartData.Core
{
    public sealed class SmartDataWriteInterceptor : SaveChangesInterceptor
    {
        private readonly IChangeUserProvider? _userProvider;
        private readonly IEventBus? _bus;
        private readonly DataOptions _options;
        private readonly ILogger<SmartDataWriteInterceptor> _logger;
        private readonly ISmartDataQueue _queue;

        // Pending items captured for the current SaveChanges call
        private readonly List<SmartWorkItem> _pending = new();

        // cache for EF value converters (for encrypted audit values)
        private static readonly ConcurrentDictionary<(IModel Model, string Prop, Type EntityType), ValueConverter?> _convCache = new();

        public SmartDataWriteInterceptor(
            DataOptions options,
            ILogger<SmartDataWriteInterceptor> logger,
            ISmartDataQueue queue,
            IEventBus? bus = null,
            IChangeUserProvider? userProvider = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _bus = bus;
            _userProvider = userProvider;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken ct = default)
        {
            _pending.Clear();

            var ctx = (DataContext)eventData.Context!;
            var entries = ctx.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToList();

            var changedBy = ResolveChangedBy();
            var now = DateTime.UtcNow;

            foreach (var e in entries)
            {
                var table = e.Metadata.GetTableName()!;
                var (keyNames, keyValues) = GetKeyParts(e);
                var entityKey = string.Join("|", keyValues.Select(v => v?.ToString() ?? ""));

                // Build change log records (encrypted via converter if present)
                var changes = BuildChangesForQueue(e);

                // Integrity props (only for added/modified + modified props)
                var integrity = BuildIntegrityForQueue(e);

                // Timeseries props (added/modified + modified props)
                var timeseries = BuildTimeseriesForQueue(e);

                // Embeddings needed?
                bool needEmbedding = _options.EnableEmbeddings
                                     && e.State != EntityState.Deleted
                                     && HasEmbeddables(e)
                                     && (e.State == EntityState.Added || EmbeddablesModified(e));

                // Publish event immediately (cheap)
                _bus?.Publish(new EntityChangeEvent
                {
                    TableName = table,
                    EntityId = entityKey,
                    Operation = e.State switch
                    {
                        EntityState.Added => EntityOperation.Insert,
                        EntityState.Modified => EntityOperation.Update,
                        EntityState.Deleted => EntityOperation.Delete,
                        _ => EntityOperation.Update
                    },
                    ChangedProperties = e.State == EntityState.Modified
                        ? e.Properties.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue))
                                       .ToDictionary(p => p.Metadata.Name, p => (p.OriginalValue!, p.CurrentValue!))
                        : new Dictionary<string, (object, object)>()
                });

                _pending.Add(new SmartWorkItem(
                    Table: table,
                    EntityKey: entityKey,
                    KeyNames: keyNames,
                    KeyValues: keyValues,
                    EntityType: e.Entity.GetType(),
                    State: e.State,
                    ChangedBy: changedBy,
                    TimestampUtc: now,
                    Changes: changes,
                    Integrity: integrity,
                    Timeseries: timeseries,
                    NeedEmbedding: needEmbedding
                ));
            }

            return await base.SavingChangesAsync(eventData, result, ct);
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken ct = default)
        {
            // Only enqueue if the save actually succeeded
            if (_pending.Count > 0)
            {
                foreach (var item in _pending)
                    _queue.Enqueue(item);
                _pending.Clear();
            }
            return base.SavedChangesAsync(eventData, result, ct);
        }

        public override Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData,
            CancellationToken ct = default)
        {
            // Drop pending items on failure (don’t process rolled-back changes)
            _pending.Clear();
            return base.SaveChangesFailedAsync(eventData, ct);
        }
        // ---------- builders for queued payloads ----------

        private static (string[] Names, object?[] Values) GetKeyParts(EntityEntry e)
        {
            var pk = e.Metadata.FindPrimaryKey();
            if (pk != null && pk.Properties.Count > 0)
            {
                var names = pk.Properties.Select(p => p.Name).ToArray();
                var vals = pk.Properties.Select(p =>
                    (e.State == EntityState.Deleted ? e.Property(p.Name).OriginalValue : e.Property(p.Name).CurrentValue)
                ).ToArray();
                return (names, vals);
            }

            // Fallback: Id/[Key]
            var idProp = e.Entity.GetType().GetProperties()
                .FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                                     p.GetCustomAttribute<KeyAttribute>() != null);
            return idProp != null
                ? (new[] { idProp.Name }, new[] { idProp.GetValue(e.Entity) })
                : (Array.Empty<string>(), Array.Empty<object?>());
        }

        private IReadOnlyList<TrackedChange> BuildChangesForQueue(EntityEntry e)
        {
            if (!_options.EnableChangeTracking) return Array.Empty<TrackedChange>();

            var props = e.Entity.GetType().GetProperties()
                .Where(p => p.GetCustomAttribute<TrackChangeAttribute>() != null)
                .ToArray();
            if (props.Length == 0) return Array.Empty<TrackedChange>();

            var list = new List<TrackedChange>();
            foreach (var p in props)
            {
                var entryProp = e.Property(p.Name);
                var state = e.State;

                var changed =
                    state == EntityState.Added ? entryProp.CurrentValue != null :
                    state == EntityState.Deleted ? entryProp.OriginalValue != null :
                                                   (entryProp.IsModified && !Equals(entryProp.OriginalValue, entryProp.CurrentValue));

                if (!changed) continue;

                var original = state == EntityState.Added ? null : ToAuditString(e, p.Name, entryProp.OriginalValue);
                var current = state == EntityState.Deleted ? null : ToAuditString(e, p.Name, entryProp.CurrentValue);

                list.Add(new TrackedChange(p.Name, original, current, state.ToString()));
            }
            return list;
        }

        private IReadOnlyList<IntegrityProp> BuildIntegrityForQueue(EntityEntry e)
        {
            if (!_options.EnableIntegrityVerification || e.State == EntityState.Deleted)
                return Array.Empty<IntegrityProp>();

            var props = e.Entity.GetType().GetProperties()
                .Where(p => p.GetCustomAttribute<EnsureIntegrityAttribute>() != null)
                .ToArray();
            if (props.Length == 0) return Array.Empty<IntegrityProp>();

            var list = new List<IntegrityProp>();
            foreach (var p in props)
            {
                var entryProp = e.Property(p.Name);
                if (e.State == EntityState.Added || entryProp.IsModified)
                {
                    var cur = entryProp.CurrentValue?.ToString() ?? string.Empty;
                    list.Add(new IntegrityProp(p.Name, cur));
                }
            }
            return list;
        }

        private IReadOnlyList<TimeseriesProp> BuildTimeseriesForQueue(EntityEntry e)
        {
            if (!_options.EnableTimeseries || e.State == EntityState.Deleted)
                return Array.Empty<TimeseriesProp>();

            var props = e.Entity.GetType().GetProperties()
                .Where(p => p.GetCustomAttribute<TimeseriesAttribute>() != null)
                .ToArray();
            if (props.Length == 0) return Array.Empty<TimeseriesProp>();

            var list = new List<TimeseriesProp>();
            foreach (var p in props)
            {
                var entryProp = e.Property(p.Name);
                if (e.State == EntityState.Added || entryProp.IsModified)
                {
                    var val = entryProp.CurrentValue?.ToString() ?? string.Empty;
                    list.Add(new TimeseriesProp(p.Name, val));
                }
            }
            return list;
        }

        private static bool HasEmbeddables(EntityEntry e)
            => e.Entity.GetType().GetCustomAttribute<EmbeddableAttribute>() != null
               || e.Entity.GetType().GetProperties().Any(p => p.GetCustomAttribute<EmbeddableAttribute>() != null);

        private static bool EmbeddablesModified(EntityEntry e)
        {
            var props = e.Entity.GetType().GetProperties()
                .Where(p => p.GetCustomAttribute<EmbeddableAttribute>() != null)
                .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return e.Properties.Any(p => props.Contains(p.Metadata.Name) && p.IsModified);
        }

        private string ResolveChangedBy()
        {
            try
            {
                var viaProvider = _userProvider?.GetCurrentUser();
                if (!string.IsNullOrWhiteSpace(viaProvider))
                    return viaProvider!;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "IChangeUserProvider.GetCurrentUser() failed; falling back.");
            }

            var principal = System.Threading.Thread.CurrentPrincipal;
            if (principal?.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(principal.Identity.Name))
                return principal.Identity.Name!;

            try
            {
                var win = System.Security.Principal.WindowsIdentity.GetCurrent()?.Name;
                if (!string.IsNullOrWhiteSpace(win)) return win!;
            }
            catch { }
            return "System";
        }

        private static string? ToAuditString(EntityEntry e, string propName, object? clrValue)
        {
            if (clrValue is null) return null;

            var key = (e.Context.Model, propName, e.Entity.GetType());
            var conv = _convCache.GetOrAdd(key, k =>
            {
                var et = e.Context.Model.FindEntityType(k.EntityType);
                var p = et?.FindProperty(k.Prop);
                return p?.GetTypeMapping()?.Converter;
            });

            if (conv != null)
            {
                var providerValue = conv.ConvertToProvider(clrValue);
                return providerValue as string ?? JsonSerializer.Serialize(providerValue);
            }
            return clrValue is string s ? s : JsonSerializer.Serialize(clrValue);
        }
    }
}
