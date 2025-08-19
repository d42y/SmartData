namespace SmartData.AnalyticsService
{
    public sealed record TenantEngineStatus(Guid TenantId, string State, DateTime? StartedAt, string? LastError);
    public sealed record AnalyticsStatus(Guid AnalyticsId, string Name, bool Enabled, DateTime? LastRun, DateTime? NextRun, string Status);

    public interface IAnalyticsEngineManager
    {
        Task StartTenantAsync(Guid tenantId, CancellationToken ct = default);
        Task StopTenantAsync(Guid tenantId, CancellationToken ct = default);
        Task RestartTenantAsync(Guid tenantId, CancellationToken ct = default);
        Task<TenantEngineStatus> GetTenantStatusAsync(Guid tenantId, CancellationToken ct = default);
        Task<IReadOnlyList<AnalyticsStatus>> ListAnalyticsAsync(Guid tenantId, CancellationToken ct = default);

        // Manual trigger (optional nodeId)
        Task RunNowAsync(Guid tenantId, Guid analyticsId, Guid? nodeId = null, IReadOnlyDictionary<string, object>? variables = null, CancellationToken ct = default);
    }
}
