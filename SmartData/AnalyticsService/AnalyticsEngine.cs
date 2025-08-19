// SmartData.AnalyticsService/AnalyticsEngine.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SmartData.Data;

namespace SmartData.AnalyticsService
{
    public interface IAnalyticsEngine
    {
        Task ExecuteAsync(Guid analyticsId, CancellationToken ct);

        Task RunOnceAsync(
            Guid tenantId,
            Guid analyticsId,
            bool isTimeDriven,
            string? triggerTable,
            IReadOnlyDictionary<string, object>? variables,
            CancellationToken ct);

    }

    /// <summary>
    /// Default engine that reuses the SmartAnalyticsService’s execution logic by
    /// calling its public API (or you can move the core methods here directly).
    /// </summary>
    public sealed class AnalyticsEngine : IAnalyticsEngine
    {
        private readonly IServiceProvider _sp;
        public AnalyticsEngine(IServiceProvider sp) => _sp = sp;

        public async Task ExecuteAsync(Guid analyticsId, CancellationToken ct)
        {
            using var scope = _sp.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<SmartAnalyticsService>(); // if you prefer, split SmartAnalyticsService into a pure runner class
            await runner.ExecuteAnalyticsAsync(analyticsId, ct);
        }

        public async Task RunOnceAsync(
            Guid tenantId,
            Guid analyticsId,
            bool isTimeDriven,
            string? triggerTable,
            IReadOnlyDictionary<string, object>? variables,
            CancellationToken ct)
        {
            using var scope = _sp.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<SmartAnalyticsService>();
            await runner.RunOnceAsync(tenantId, analyticsId, isTimeDriven, triggerTable, variables, ct);
        }
    }
}
