namespace SmartData.Models
{
    public sealed record IntegrityPropertyStatus(
            string Property,
            string? LatestHash,          // hash stored in __sysIntegrityLog
            string? RecomputedHash,      // hash recomputed from provided currentValue
            DateTime? LatestTimestampUtc,
            bool? Matches                // null = no reference hash; otherwise match result
        );

    public sealed record IntegrityCheckResult(
        string TableName,
        string EntityId,
        IReadOnlyList<IntegrityPropertyStatus> Properties
    );
}
