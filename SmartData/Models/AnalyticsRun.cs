namespace SmartData.Models
{
    public class AnalyticsRun
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public Guid AnalyticsId { get; set; }
        public Guid? NodeId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string Status { get; set; } = "Running"; // Running|OK|Error|Skipped
        public string? Error { get; set; }
    }
}
