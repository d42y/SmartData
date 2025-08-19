using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Data;
using SmartData.Models;

namespace SmartData.AnalyticsService
{
    /// <summary>
    /// Executes analytics and persists outputs when configured.
    /// Uses existing SmartAnalyticsService logic, with a small extension to capture context.
    /// </summary>
    internal static class AnalyticsRunner
    {
        public static async Task RunForAllBindingsAsync(
            IServiceProvider sp,
            Guid tenantId,
            Guid analyticsId,
            Guid? nodeId,
            IReadOnlyDictionary<string, object>? variables,
            CancellationToken ct)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataContext>();
            var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AnalyticsRunner");

            var analytic = await db.Set<Analytics>().FirstOrDefaultAsync(a => a.Id == analyticsId && a.TenantId == tenantId, ct);
            if (analytic == null || !analytic.Enabled) return;

            // Find bindings (either a specific node or all enabled)
            var bindingsQuery = db.Set<AnalyticsBinding>()
                .Where(b => b.TenantId == tenantId && b.AnalyticsId == analyticsId && b.Enabled);
            var bindings = nodeId.HasValue
                ? await bindingsQuery.Where(b => b.NodeId == nodeId.Value).ToListAsync(ct)
                : await bindingsQuery.ToListAsync(ct);

            // If no bindings, still run once (global analytic) with no NodeId
            if (bindings.Count == 0)
                bindings = new List<AnalyticsBinding> { new AnalyticsBinding { TenantId = tenantId, AnalyticsId = analyticsId, NodeId = Guid.Empty, Enabled = true } };

            foreach (var b in bindings)
            {
                var runId = Guid.NewGuid();
                await db.AddAsync(new AnalyticsRun
                {
                    Id = runId,
                    TenantId = tenantId,
                    AnalyticsId = analyticsId,
                    NodeId = b.NodeId == Guid.Empty ? null : b.NodeId,
                    StartedAt = DateTime.UtcNow,
                    Status = "Running"
                }, ct);
                await db.SaveChangesAsync(ct);

                try
                {
                    // Execute using existing service (we extend it to expose context)
                    var svc = scope.ServiceProvider.GetRequiredService<SmartAnalyticsService>();
                    var result = await svc.ExecuteAnalyticsWithContextAsync(analyticsId, ct); // NEW METHOD (see next section)

                    // Persist outputs if configured
                    if (analytic.OutputMode.Equals("Table", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(analytic.OutputTable) &&
                        string.Equals(analytic.OutputTable, "Variables", StringComparison.OrdinalIgnoreCase) &&
                        b.NodeId != Guid.Empty)
                    {
                        // Resolve client-provided sink and delegate the write
                        var sink = scope.ServiceProvider.GetService<IAnalyticsOutputSink>();
                        if (sink is null)
                        {
                            log.LogWarning("Analytics OutputMode='Table' requested but no IAnalyticsOutputSink is registered. Skipping variable persistence.");
                        }
                        else
                        {
                            await sink.UpsertVariablesAsync(tenantId, b.NodeId, result.Context, ct);
                        }
                    }


                    // Update analytics row (Value/LastRun/Status already set by service)
                    var row = await db.Set<Analytics>().FirstAsync(x => x.Id == analyticsId, ct);
                    row.NextRun = row.Interval > 0 ? DateTime.UtcNow.AddSeconds(row.Interval) : null;
                    await db.SaveChangesAsync(ct);

                    var run = await db.Set<AnalyticsRun>().FirstAsync(r => r.Id == runId, ct);
                    run.FinishedAt = DateTime.UtcNow;
                    run.Status = "OK";
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Analytics execution failed (Tenant={TenantId}, Analytics={AnalyticsId}, Node={NodeId})",
                        tenantId, analyticsId, b.NodeId);

                    var run = await db.Set<AnalyticsRun>().FirstAsync(r => r.Id == runId, ct);
                    run.FinishedAt = DateTime.UtcNow;
                    run.Status = "Error";
                    run.Error = ex.Message;
                    await db.SaveChangesAsync(ct);
                }
            }
        }

      
    }
}
