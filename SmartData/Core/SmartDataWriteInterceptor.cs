// SmartData.Core/SmartDataWriteInterceptor.cs
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging;
using SmartData.AnalyticsService;
using SmartData.Core.Queue;                // IWorkRouter + SmartItem types
using SmartData.Data;
using SmartData.Models;

namespace SmartData.Core
{
    /// <summary>
    /// Captures changes during SaveChanges and emits lane-specific SmartItems
    /// to the router (ChangeLogItem, IntegrityItem, TimeseriesItem, EmbeddingItem).
    /// The lanes handle coalescing, batching, and persistence.
    /// </summary>
    public sealed class SmartDataWriteInterceptor : SaveChangesInterceptor
    {
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _trackedPropsCache = new();
        private const int AuditMaxLen = 4000; // keep well under nvarchar(max) to avoid log spam

        private readonly IChangeUserProvider? _userProvider;
        private readonly IEventBus? _bus;
        private readonly DataOptions _options;
        private readonly ILogger<SmartDataWriteInterceptor> _logger;
        private readonly IWorkRouter _router;

        // Pending items for the *current* SaveChanges call (emitted on success)
        private readonly List<SmartItem> _pending = new();

        // cache for EF value converters (for encrypted audit values)
        private static readonly ConcurrentDictionary<(IModel Model, string Prop, Type EntityType), ValueConverter?> _convCache = new();

        public SmartDataWriteInterceptor(
            DataOptions options,
            ILogger<SmartDataWriteInterceptor> logger,
            IWorkRouter router,
            IEventBus? bus = null,
            IChangeUserProvider? userProvider = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _bus = bus;
            _userProvider = userProvider;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken ct = default)
        {
            _pending.Clear();

            if (eventData.Context is not DataContext ctx)
                return await base.SavingChangesAsync(eventData, result, ct);

            var entries = ctx.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToList();

            if (entries.Count == 0)
                return await base.SavingChangesAsync(eventData, result, ct);

            var changedBy = ResolveChangedBy();
            var now = DateTime.UtcNow;

            foreach (var e in entries)
            {
                var table = e.Metadata.GetTableName() ?? e.Metadata.ClrType.Name;
                var (keyNames, keyValues) = GetKeyParts(e);
                var entityKey = string.Join("|", keyValues.Select(v => v?.ToString() ?? string.Empty));

                // CHANGE LOG (row-per-change as you prefer)
                var changeLogItems = BuildChangeLogItems(e, table, entityKey, changedBy, now);
                if (changeLogItems is not null)
                    _pending.Add(changeLogItems);

                // INTEGRITY
                var integrityItem = BuildIntegrityItem(e, table, entityKey, changedBy, now);
                if (integrityItem is not null)
                    _pending.Add(integrityItem);

                // TIMESERIES
                var timeseriesItem = BuildTimeseriesItem(e, table, entityKey, changedBy, now);
                if (timeseriesItem is not null)
                    _pending.Add(timeseriesItem);

                // EMBEDDINGS
                var embeddingItem = BuildEmbeddingItem(e, table, entityKey, changedBy, keyValues, now);
                if (embeddingItem is not null)
                    _pending.Add(embeddingItem);

                // EVENT BUS (unchanged, fire-and-forget style)
                PublishEntityChangeEvent(e, table, entityKey);
            }

            return await base.SavingChangesAsync(eventData, result, ct);
        }

        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken ct = default)
        {
            // Only enqueue if the save actually succeeded
            if (result > 0 && _pending.Count > 0)
            {
                try
                {
                    // Route all pending items; lanes will batch/flush later
                    var tasks = _pending.Select(item => _router.EnqueueAsync(item, ct).AsTask());
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to enqueue SmartData items after SaveChanges.");
                    // We don't rethrow to avoid failing the already-committed transaction.
                }
                finally
                {
                    _pending.Clear();
                }
            }

            return await base.SavedChangesAsync(eventData, result, ct);
        }

        public override Task SaveChangesFailedAsync(
            DbContextErrorEventData eventData,
            CancellationToken ct = default)
        {
            // Drop pending items on failure (don’t process rolled-back changes)
            _pending.Clear();
            return base.SaveChangesFailedAsync(eventData, ct);
        }

        private static PropertyInfo[] GetTrackedProps(Type t) =>
    _trackedPropsCache.GetOrAdd(t, tt =>
        tt.GetProperties()
          .Where(p => p.GetCustomAttribute<TrackChangeAttribute>() != null)
          .ToArray());

        private static string? NormalizeForAudit(EntityEntry e, string propName, object? value)
        {
            if (value is null) return null;

            // Use your existing EF value converter / encryption path
            var s = ToAuditString(e, propName, value);
            if (string.IsNullOrEmpty(s)) return s;

            // If it looks like JSON, minify to reduce noise (whitespace, pretty-print, etc.)
            var trimmed = s.TrimStart();
            if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    s = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false });
                }
                catch
                {
                    // non-fatal: leave as-is if not valid JSON
                }
            }

            // Truncate very large values but include a hash for later verification
            if (s.Length > AuditMaxLen)
            {
                var hash = ComputeSha256(s);
                // include a suffix so you can correlate later, but keep payload small
                s = s.Substring(0, AuditMaxLen) + $"… [sha256:{hash}]";
            }
            return s;
        }

        private static string ComputeSha256(string s)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)));
        }

        // ---------- builders for lane payloads ----------

        private ChangeLogItem? BuildChangeLogItems(
     EntityEntry e, string table, string entityKey, string changedBy, DateTime now)
        {
            if (!_options.EnableChangeTracking) return null;

            var trackedProps = GetTrackedProps(e.Entity.GetType());
            if (trackedProps.Length == 0) return null;

            var changes = new List<PropertyChange>(capacity: trackedProps.Length);

            foreach (var p in trackedProps)
            {
                var ep = e.Property(p.Name);
                var state = e.State;

                // Only emit when the property’s value truly changed
                // Added: record if current != null
                // Deleted: record if original != null
                // Modified: record if IsModified AND values differ
                bool changed =
                    state == EntityState.Added
                        ? ep.CurrentValue is not null
                        : state == EntityState.Deleted
                            ? ep.OriginalValue is not null
                            : (ep.IsModified && !Equals(ep.OriginalValue, ep.CurrentValue));

                if (!changed) continue;

                var original = state == EntityState.Added
                    ? null
                    : NormalizeForAudit(e, p.Name, ep.OriginalValue);

                var current = state == EntityState.Deleted
                    ? null
                    : NormalizeForAudit(e, p.Name, ep.CurrentValue);

                // guard against “changes” that normalize to the same text
                if (original == current) continue;

                changes.Add(new PropertyChange(
                    Property: p.Name,
                    Original: original,
                    New: current,
                    ChangeType: state.ToString()));
            }

            return changes.Count == 0
                ? null
                : new ChangeLogItem(table, entityKey, now, changedBy, changes);
        }


        private IntegrityItem? BuildIntegrityItem(
    EntityEntry e, string table, string entityKey, string changedBy, DateTime now)
        {
            if (!_options.EnableIntegrityVerification || e.State == EntityState.Deleted)
                return null;

            var props = GetIntegrityProps(e.Entity.GetType());
            if (props.Length == 0) return null;

            var list = new List<IntegrityChange>(capacity: props.Length);
            foreach (var p in props)
            {
                var ep = e.Property(p.Name);
                if (e.State == EntityState.Added || ep.IsModified)
                {
                    var cur = ep.CurrentValue?.ToString() ?? string.Empty;
                    // keep input deterministic to hashing later in the pipeline
                    list.Add(new IntegrityChange(p.Name, NormalizeString(cur)));
                }
            }

            return list.Count == 0 ? null : new IntegrityItem(table, entityKey, now, changedBy, list);
        }


        private TimeseriesItem? BuildTimeseriesItem(
    EntityEntry e, string table, string entityKey, string changedBy, DateTime now)
        {
            if (!_options.EnableTimeseries || e.State == EntityState.Deleted)
                return null;

            var props = GetTimeseriesProps(e.Entity.GetType());
            if (props.Length == 0) return null;

            var points = new List<TimeseriesPoint>(capacity: props.Length);
            foreach (var p in props)
            {
                var ep = e.Property(p.Name);
                if (e.State == EntityState.Added || ep.IsModified)
                {
                    var val = ep.CurrentValue?.ToString() ?? string.Empty;
                    points.Add(new TimeseriesPoint(Property: p.Name, Value: NormalizeString(val), TimestampUtc: now));
                }
            }

            return points.Count == 0 ? null : new TimeseriesItem(table, entityKey, now, changedBy, points);
        }


        private EmbeddingItem? BuildEmbeddingItem(
    EntityEntry e, string table, string entityKey, string changedBy, object?[] keyValues, DateTime now)
        {
            if (!_options.EnableEmbeddings || e.State == EntityState.Deleted)
                return null;

            var t = e.Entity.GetType();
            var embeddableProps = GetEmbeddableProps(t);
            var hasMeta = TypeHasEmbeddableMeta(t);

            if (!hasMeta && embeddableProps.Length == 0)
                return null; // no embeddable markers anywhere

            bool needEmbedding = e.State == EntityState.Added
                                 || AnyPropModified(e, embeddableProps);

            if (!needEmbedding) return null;

            return new EmbeddingItem(
                Table: table,
                EntityKey: entityKey,
                Utc: now,
                Actor: changedBy,
                EntityType: t,
                KeyValues: keyValues,
                NeedEmbedding: true
            );
        }

        private AnalyticsItem? BuildAnalyticsItem(EntityEntry e, string table, string entityKey, string changedBy, DateTime now)
        {
            if (!_options.EnableAnalytics) return null;

            // Only consider [TrackChange] properties
            var props = e.Entity.GetType().GetProperties()
                .Where(p => p.GetCustomAttribute<TrackChangeAttribute>() != null)
                .Select(p => p.Name)
                .ToArray();

            if (props.Length == 0) return null;

            var changedPropNames = new List<string>();
            foreach (var name in props)
            {
                var ep = e.Property(name);
                var state = e.State;

                bool changed =
                    state == EntityState.Added ? ep.CurrentValue != null :
                    state == EntityState.Deleted ? ep.OriginalValue != null :
                    (ep.IsModified && !Equals(ep.OriginalValue, ep.CurrentValue));

                if (changed) changedPropNames.Add(name);
            }

            if (changedPropNames.Count == 0) return null;

            // If you have a tenant in your entities, fetch it here. Otherwise pass null.
            Guid? tenantId = TryGetTenantId(e.Entity);

            return new AnalyticsItem(
                Table: table,
                EntityKey: entityKey,
                Utc: now,
                Actor: changedBy,
                TenantId: tenantId.Value,
                AnalyticsId: Guid.NewGuid(),
                IsTimeDriven:false,
                TriggerTable: "table",
                null);
        }

        // add a helper if you want per-tenant analytics
        private static Guid? TryGetTenantId(object entity)
        {
            var p = entity.GetType().GetProperty("TenantId");
            if (p != null && p.PropertyType == typeof(Guid))
            {
                var v = (Guid?)p.GetValue(entity);
                if (v.HasValue && v.Value != Guid.Empty) return v;
            }
            return null;
        }

        private void PublishEntityChangeEvent(EntityEntry e, string table, string entityKey)
        {
            try
            {
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
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "EntityChangeEvent publish failed (non-fatal).");
            }
        }

        // ---------- helpers ----------
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _integrityPropsCache = new();
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _timeseriesPropsCache = new();
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _embeddablePropsCache = new();
        private static readonly ConcurrentDictionary<Type, bool> _typeHasEmbeddableMetaCache = new();

        private static PropertyInfo[] GetIntegrityProps(Type t) =>
            _integrityPropsCache.GetOrAdd(t, tt =>
                tt.GetProperties().Where(p => p.GetCustomAttribute<EnsureIntegrityAttribute>() != null).ToArray());

        private static PropertyInfo[] GetTimeseriesProps(Type t) =>
            _timeseriesPropsCache.GetOrAdd(t, tt =>
                tt.GetProperties().Where(p => p.GetCustomAttribute<TimeseriesAttribute>() != null).ToArray());

        private static PropertyInfo[] GetEmbeddableProps(Type t) =>
            _embeddablePropsCache.GetOrAdd(t, tt =>
                tt.GetProperties().Where(p => p.GetCustomAttribute<EmbeddableAttribute>() != null).ToArray());

        private static bool TypeHasEmbeddableMeta(Type t) =>
            _typeHasEmbeddableMetaCache.GetOrAdd(t, tt =>
                tt.GetCustomAttribute<EmbeddableAttribute>() != null);

        private static bool AnyPropModified(EntityEntry e, IEnumerable<PropertyInfo> props)
        {
            var lookup = props.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var ep in e.Properties)
                if (lookup.Contains(ep.Metadata.Name) && ep.IsModified) return true;
            return false;
        }

        // Optional: minify JSON-ish strings to reduce noise
        private static string NormalizeString(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            var t = s.AsSpan().TrimStart();
            if (t.Length > 0 && (t[0] == '{' || t[0] == '['))
            {
                try
                {
                    using var doc = JsonDocument.Parse(s);
                    return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false });
                }
                catch { /* leave as-is */ }
            }
            return s;
        }

        private static (string[] Names, object?[] Values) GetKeyParts(EntityEntry e)
        {
            var pk = e.Metadata.FindPrimaryKey();
            if (pk != null && pk.Properties.Count > 0)
            {
                var names = pk.Properties.Select(p => p.Name).ToArray();
                var vals = pk.Properties.Select(p =>
                    (e.State == EntityState.Deleted ? e.Property(p.Name).OriginalValue : e.Property(p.Name).CurrentValue)
                ).ToArray();
                return (names, vals!);
            }

            // Fallback: Id/[Key]
            var idProp = e.Entity.GetType().GetProperties()
                .FirstOrDefault(p => p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                                     p.GetCustomAttribute<KeyAttribute>() != null);
            return idProp != null
                ? (new[] { idProp.Name }, new[] { idProp.GetValue(e.Entity) })
                : (Array.Empty<string>(), Array.Empty<object?>());
        }

        //private static bool HasEmbeddables(EntityEntry e)
        //    => e.Entity.GetType().GetCustomAttribute<EmbeddableAttribute>() != null
        //       || e.Entity.GetType().GetProperties().Any(p => p.GetCustomAttribute<EmbeddableAttribute>() != null);

        //private static bool EmbeddablesModified(EntityEntry e)
        //{
        //    var props = e.Entity.GetType().GetProperties()
        //        .Where(p => p.GetCustomAttribute<EmbeddableAttribute>() != null)
        //        .Select(p => p.Name)
        //        .ToHashSet(StringComparer.OrdinalIgnoreCase);

        //    return e.Properties.Any(p => props.Contains(p.Metadata.Name) && p.IsModified);
        //}

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
                _logger.LogDebug(ex, "IChangeUserProvider.GetCurrentUser() failed; falling back.");
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
