namespace SmartData.Core.Queue.Strategies
{
    /// <summary>
    /// A batching strategy used by a Lane<T>. The lane accumulates items and
    /// delivers them in batches according to MaxBatch/MaxWait.
    /// </summary>
    public interface IBatchStrategy<TItem>
    {
        /// A short name for logs/metrics.
        string Name { get; }

        /// Maximum number of items per batch before the lane flushes immediately.
        int MaxBatch { get; }

        /// Maximum time to wait before flushing a (possibly partial) batch.
        TimeSpan MaxWait { get; }

        /// Process a flushed batch.
        ValueTask OnNextAsync(IReadOnlyList<TItem> batch, CancellationToken ct);
    }

    /// <summary>
    /// Convenience base with sensible defaults. Override as needed.
    /// </summary>
    public abstract class BatchStrategyBase<TItem> : IBatchStrategy<TItem>
    {
        public virtual string Name => GetType().Name;
        public virtual int MaxBatch => 1024;
        public virtual TimeSpan MaxWait => TimeSpan.FromSeconds(10);

        public abstract ValueTask OnNextAsync(IReadOnlyList<TItem> batch, CancellationToken ct);
    }
}
