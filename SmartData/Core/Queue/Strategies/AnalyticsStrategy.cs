using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.AnalyticsService;

namespace SmartData.Core.Queue.Strategies
{
    public sealed class AnalyticsStrategy : ILaneStrategy<AnalyticsItem>
    {
        private readonly IServiceProvider _sp;
        private readonly ILogger<AnalyticsStrategy> _log;

        public int MaxBatch => 512;                // smallish, analytics can be heavier than logging
        public TimeSpan MaxWait => TimeSpan.FromSeconds(2);

        public AnalyticsStrategy(IServiceProvider sp, ILogger<AnalyticsStrategy> log)
        {
            _sp = sp;
            _log = log;
        }

        // Coalesce by (TenantId, AnalyticsId) — keep the newest trigger info
        public bool TryCoalesce(AnalyticsItem existing, AnalyticsItem incoming, out AnalyticsItem merged)
        {
            if (existing.TenantId == incoming.TenantId &&
                existing.AnalyticsId == incoming.AnalyticsId)
            {
                // prefer the most recent and prefer an event trigger (more specific)
                var chosen = incoming.Utc >= existing.Utc ? incoming : existing;
                if (!chosen.IsTimeDriven && (incoming.TriggerTable is { Length: > 0 }))
                    chosen = chosen with { TriggerTable = incoming.TriggerTable };

                // merge variables (incoming wins on conflicts)
                IEnumerable<KeyValuePair<string, object>> existingVars =
                    existing.Variables ?? Enumerable.Empty<KeyValuePair<string, object>>();
                IEnumerable<KeyValuePair<string, object>> incomingVars =
                    incoming.Variables ?? Enumerable.Empty<KeyValuePair<string, object>>();

                var mergedVars = existingVars
                    .Concat(incomingVars)
                    .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

                merged = chosen with { Variables = mergedVars };
                return true;
            }

            merged = default!;
            return false;
        }

        public async Task PersistAsync(IReadOnlyList<AnalyticsItem> batch, CancellationToken ct)
        {
            if (batch.Count == 0) return;

            // Group to one run per (tenant, analytic)
            var groups = batch
                .GroupBy(i => (i.TenantId, i.AnalyticsId))
                .Select(g => g.OrderByDescending(x => x.Utc).First()) // newest
                .ToList();

            using var scope = _sp.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IAnalyticsEngineManager>();
            foreach (var item in groups)
            {
                try
                {
                    await manager.RunNowAsync(
                        tenantId: item.TenantId,
                        analyticsId: item.AnalyticsId,
                        nodeId: null,                    // let the manager fan-out to all bindings
                        variables: item.Variables,
                        ct: ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Analytics run failed (Tenant={TenantId}, Analytics={AnalyticsId})",
                        item.TenantId, item.AnalyticsId);
                }
            }
        }
    }
}
