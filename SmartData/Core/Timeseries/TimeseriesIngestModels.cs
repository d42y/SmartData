namespace SmartData.Core.Timeseries
{
    public sealed record IngestPoint(
        string TableName,
        string EntityId,
        string PropertyName,
        string Value,              
        DateTime TimestampUtc);

    public sealed class TimeseriesIngestOptions
    {
        /// Max points flushed per SQL transaction.
        public int MaxBatchSize { get; set; } = 20_000;

        /// Max wait before flushing a partial batch.
        public TimeSpan MaxWait { get; set; } = TimeSpan.FromMilliseconds(400);

        /// Bounded queue capacity (points). Tune to expected bursts.
        public int ChannelCapacity { get; set; } = 1_000_000;

        /// Parallel flushers (partitioned by key hash to reduce contention).
        public int DegreeOfParallelism { get; set; } = Math.Max(2, Environment.ProcessorCount / 2);
    }
}
