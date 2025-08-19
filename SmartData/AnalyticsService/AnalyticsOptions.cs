namespace SmartData.AnalyticsService
{
    public sealed class AnalyticsOptions
    {
        /// <summary>Optional: the "scope" entity for analytics (e.g., Nodes).</summary>
        public string ScopeTable { get; set; } = "Nodes";

        /// <summary>Optional: the output table to persist variables (e.g., Variables).</summary>
        public string? OutputTable { get; set; } = null;

        /// <summary>Default output behavior: "Memory" or "Table".</summary>
        public string DefaultOutputMode { get; set; } = "Memory";
    }
}
