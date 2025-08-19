using Microsoft.EntityFrameworkCore.Metadata.Internal;
using SmartData.Models;

namespace SmartData.Core.Queue
{
    
    public sealed class SmartWorkItem
    {
        // Legacy shape — keep fields you need
        public string Table { get; set; } = default!;
        public string EntityKey { get; set; } = default!;
        public DateTime TimestampUtc { get; set; }
        public string? ChangedBy { get; set; }
        public List<PropertyChange> Changes { get; set; } = new();
        public List<IntegrityChange> Integrity { get; set; } = new();
        public List<TimeseriesPoint> Timeseries { get; set; } = new();
        public bool NeedEmbedding { get; set; }
        public Type? EntityType { get; set; }
        public object[] KeyValues { get; set; } = Array.Empty<object>();
    }

    // Adapter: take legacy work items and push them into the router as specific items
    public sealed class SmartQueueToRouterAdapter
    {
        private readonly IWorkRouter _router;
        public SmartQueueToRouterAdapter(IWorkRouter router) => _router = router;

        public async Task EnqueueAsync(SmartWorkItem w, CancellationToken ct = default)
        {
            if (w.Changes.Count > 0)
                await _router.EnqueueAsync(new ChangeLogItem(w.Table, w.EntityKey, w.TimestampUtc, w.ChangedBy, w.Changes), ct);

            if (w.Integrity.Count > 0)
                await _router.EnqueueAsync(new IntegrityItem(w.Table, w.EntityKey, w.TimestampUtc, w.ChangedBy, w.Integrity), ct);

            if (w.Timeseries.Count > 0)
                await _router.EnqueueAsync(new TimeseriesItem(w.Table, w.EntityKey, w.TimestampUtc, w.ChangedBy, w.Timeseries), ct);

            if (w.NeedEmbedding && w.EntityType != null)
                await _router.EnqueueAsync(new EmbeddingItem(w.Table, w.EntityKey, w.TimestampUtc, w.ChangedBy, w.EntityType, w.KeyValues, true), ct);
            
            
        }
    }
}
