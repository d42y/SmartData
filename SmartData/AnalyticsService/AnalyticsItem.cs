using SmartData.Core.Queue;

namespace SmartData.AnalyticsService
{
    public sealed record AnalyticsItem(
        // SmartItem base fields:
        string Table,               // use "__sysAnalytics"
        string EntityKey,           // analyticsId.ToString()
        DateTime Utc,
        string Actor,

        // Analytics-specific:
        Guid TenantId,
        Guid AnalyticsId,
        bool IsTimeDriven,          // true = interval-based; false = event-triggered
        string? TriggerTable,       // optional triggering table name when event-triggered
        IReadOnlyDictionary<string, object>? Variables // optional ambient variables
    ) : SmartItem(Table, EntityKey, Utc, Actor);
}
