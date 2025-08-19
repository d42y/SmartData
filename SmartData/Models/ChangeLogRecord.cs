namespace SmartData.Models
{
    public class ChangeLogRecord
    {
        public Guid Id { get; set; }
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public string ChangedBy { get; set; }
        public string PropertyName { get; set; }
        public DateTime ChangedAt { get; set; }
        public string? OriginalValue { get; set; }
        public string? NewValue { get; set; }
        public string ChangeType { get; set; }
    }

    public sealed record PropertyChange(string Property, string? Original, string? New, string ChangeType);
    public sealed record IntegrityChange(string Property, string CurrentValue);
    public sealed record TimeseriesPoint(string Property, string Value, DateTime TimestampUtc);
}