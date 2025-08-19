namespace SmartData.Core.Queue
{
    public interface ILaneStrategy<TItem> where TItem : SmartItem
    {
        int MaxBatch { get; }          // e.g., 4096
        TimeSpan MaxWait { get; }      // e.g., 10s for ChangeLog
        bool TryCoalesce(TItem existing, TItem incoming, out TItem merged);
        Task PersistAsync(IReadOnlyList<TItem> batch, CancellationToken ct);
    }

    public interface IWorkRouter
    {
        ValueTask EnqueueAsync(SmartItem item, CancellationToken ct);
        void Complete();
        Task Completion { get; }
    }
}
