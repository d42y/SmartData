namespace SmartData.Models
{
    public class AnalyticsBinding
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public Guid NodeId { get; set; }
        public Guid AnalyticsId { get; set; }
        public bool Enabled { get; set; } = true;
    }
}
