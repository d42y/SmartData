namespace SmartData.Models
{
    /// <summary>
    /// Explicit event trigger for an Analytics definition.
    /// Uses __sysChangeLog as the event source.
    /// - TableName: required
    /// - PropertyName: required
    /// - EntityId: optional (if null/empty = any entity)
    /// - ChangeType: optional (if null/empty = any type)
    /// </summary>
    public sealed class AnalyticsTrigger
    {
        public Guid Id { get; set; }
        public Guid AnalyticsId { get; set; }
        public string TableName { get; set; } = default!;
        public string PropertyName { get; set; } = default!;
        public string? EntityId { get; set; }
        public string? ChangeType { get; set; }
    }
}
