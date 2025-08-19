using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartData.Core;
using SmartData.Data;
using SmartData.Models;
using System.Collections.Concurrent;

namespace SmartData.AnalyticsService
{
    /// <summary>Singleton control plane. Spawns/controls a TenantEngine per tenant on demand.</summary>
    public sealed class AnalyticsEngineManager : IAnalyticsEngineManager
    {
        private readonly IServiceProvider _sp;
        private readonly DataOptions _opts;
        private readonly ILogger<AnalyticsEngineManager> _log;

        private sealed class TenantEngine
        {
            public Guid TenantId { get; }
            public CancellationTokenSource? Cts { get; private set; }
            public Task? Loop { get; private set; }
            public DateTime? StartedAt { get; private set; }
            public string? LastError { get; private set; }

            private readonly IServiceProvider _sp;
            private readonly DataOptions _opts;
            private readonly ILogger _log;

            public TenantEngine(Guid tenantId, IServiceProvider sp, DataOptions opts, ILogger log)
            {
                TenantId = tenantId; _sp = sp; _opts = opts; _log = log;
            }

            public void Start()
            {
                if (Loop != null && !Loop.IsCompleted) return;
                Cts = new CancellationTokenSource();
                StartedAt = DateTime.UtcNow;
                Loop = Task.Run(() => RunLoopAsync(Cts.Token));
            }

            public async Task StopAsync()
            {
                if (Cts == null) return;
                try { Cts.Cancel(); }
                finally
                {
                    try { if (Loop != null) await Loop; } catch { /* ignore */ }
                    Cts.Dispose(); Cts = null; Loop = null;
                }
            }

            // Interval scheduler per tenant (kept simple: every 5s look for due analytics)
            private async Task RunLoopAsync(CancellationToken ct)
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var scope = _sp.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<DataContext>();

                        var due = await db.Set<Analytics>()
                            .Where(a => a.TenantId == TenantId && a.Enabled && a.Interval > 0)
                            .ToListAsync(ct);

                        foreach (var a in due)
                        {
                            var shouldRun =
                                (!a.LastRun.HasValue) ||
                                (a.Interval > 0 && (DateTime.UtcNow - a.LastRun.Value).TotalSeconds >= a.Interval) ||
                                (a.NextRun.HasValue && a.NextRun.Value <= DateTime.UtcNow);

                            if (!shouldRun) continue;

                            await AnalyticsRunner.RunForAllBindingsAsync(_sp, a.TenantId, a.Id, null, null, ct);

                            // update NextRun
                            using var scope2 = _sp.CreateScope();
                            var db2 = scope2.ServiceProvider.GetRequiredService<DataContext>();
                            var row = await db2.Set<Analytics>().FirstAsync(x => x.Id == a.Id, ct);
                            row.LastRun = DateTime.UtcNow;
                            row.NextRun = row.Interval > 0 ? DateTime.UtcNow.AddSeconds(row.Interval) : null;
                            await db2.SaveChangesAsync(ct);
                        }
                    }
                    catch (OperationCanceledException) { /* normal */ }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        _log.LogError(ex, "Tenant analytics loop error (TenantId={TenantId})", TenantId);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }

        private readonly ConcurrentDictionary<Guid, TenantEngine> _tenants = new();

        public AnalyticsEngineManager(IServiceProvider sp, DataOptions opts, ILogger<AnalyticsEngineManager> log)
        { _sp = sp; _opts = opts; _log = log; }

        private TenantEngine GetOrCreate(Guid tenantId)
        {
            return _tenants.GetOrAdd(tenantId, id =>
            {
                var logger = _sp.GetRequiredService<ILoggerFactory>().CreateLogger($"TenantEngine[{id}]");
                return new TenantEngine(id, _sp, _opts, logger);
            });
        }

        public Task StartTenantAsync(Guid tenantId, CancellationToken ct = default)
        {
            GetOrCreate(tenantId).Start();
            return Task.CompletedTask;
        }

        public Task StopTenantAsync(Guid tenantId, CancellationToken ct = default)
        {
            return GetOrCreate(tenantId).StopAsync();
        }

        public async Task RestartTenantAsync(Guid tenantId, CancellationToken ct = default)
        {
            await StopTenantAsync(tenantId, ct);
            await StartTenantAsync(tenantId, ct);
        }

        public Task<TenantEngineStatus> GetTenantStatusAsync(Guid tenantId, CancellationToken ct = default)
        {
            var t = GetOrCreate(tenantId);
            var running = t.Loop != null && !t.Loop.IsCompleted ? "Running" : "Stopped";
            return Task.FromResult(new TenantEngineStatus(tenantId, running, t.StartedAt, t.LastError));
        }

        public async Task<IReadOnlyList<AnalyticsStatus>> ListAnalyticsAsync(Guid tenantId, CancellationToken ct = default)
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            var rows = await db.Set<Analytics>().Where(a => a.TenantId == tenantId).ToListAsync(ct);
            return rows.Select(a => new AnalyticsStatus(a.Id, a.Name, a.Enabled, a.LastRun, a.NextRun, a.Status)).ToList();
        }

        public Task RunNowAsync(Guid tenantId, Guid analyticsId, Guid? nodeId = null, IReadOnlyDictionary<string, object>? variables = null, CancellationToken ct = default)
        {
            return AnalyticsRunner.RunForAllBindingsAsync(_sp, tenantId, analyticsId, nodeId, variables, ct);
        }
    }
}
