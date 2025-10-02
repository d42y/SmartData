using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Core.Queue.Writers;
using SmartData.Models;

namespace SmartData.Core.Queue.Strategies
{
    public sealed class TimeseriesStrategy : ILaneStrategy<TimeseriesItem>
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TimeseriesStrategy> _log;

        public TimeseriesStrategy(IServiceScopeFactory scopeFactory, ILogger<TimeseriesStrategy> log)
        {
            _scopeFactory = scopeFactory;
            _log = log;
        }

        public int MaxBatch => 2048;
        public TimeSpan MaxWait => TimeSpan.FromMilliseconds(500);

        public bool TryCoalesce(TimeseriesItem a, TimeseriesItem b, out TimeseriesItem merged)
        {
            var list = new List<TimeseriesPoint>(a.Points.Count + b.Points.Count);
            list.AddRange(a.Points);
            list.AddRange(b.Points);
            merged = a with { Utc = b.Utc, Points = list };
            return true;
        }

        public async Task PersistAsync(IReadOnlyList<TimeseriesItem> batch, CancellationToken ct)
        {
            if (batch is null || batch.Count == 0) return;

            static string VNorm(string v)
                => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d.ToString("G17", CultureInfo.InvariantCulture)
                    : v?.Trim() ?? string.Empty;

            var grouped = batch
                .SelectMany(i => i.Points.Select(p => new
                {
                    i.Table,
                    i.EntityKey,
                    p.Property,
                    ValueKey = VNorm(p.Value),
                    p.TimestampUtc
                }))
                .GroupBy(k => new { k.Table, k.EntityKey, k.Property, k.ValueKey })
                .Select(g =>
                {
                    var ordered = g.Select(x => x.TimestampUtc)
                                   .Distinct()
                                   .OrderBy(x => x)
                                   .ToList();

                    return (
                        Table: g.Key.Table,
                        EntityId: g.Key.EntityKey,
                        Property: g.Key.Property,
                        Value: g.Key.ValueKey,
                        BaseTs: ordered[0],
                        Deltas: (IReadOnlyList<DateTime>)ordered
                    );
                })
                .ToList();

            using var scope = _scopeFactory.CreateScope();
            var writer = scope.ServiceProvider.GetRequiredService<TimeseriesEfBatchWriter>();

            try
            {
                await writer.WriteAsync(grouped, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Timeseries batch persist failed. Groups={Count}", grouped.Count);
                throw;
            }
        }
    }
}
