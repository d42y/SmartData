using SmartData.Models;


namespace SmartData.Core.Queue
{
    public abstract record SmartItem(string Table, string EntityKey, DateTime Utc, string? Actor);

    public sealed record ChangeLogItem(
        string Table, string EntityKey, DateTime Utc, string? Actor,
        IReadOnlyList<PropertyChange> Changes) : SmartItem(Table, EntityKey, Utc, Actor);

    public sealed record IntegrityItem(
        string Table, string EntityKey, DateTime Utc, string? Actor,
        IReadOnlyList<IntegrityChange> Props) : SmartItem(Table, EntityKey, Utc, Actor);

    public sealed record EmbeddingItem(
        string Table, string EntityKey, DateTime Utc, string? Actor,
        Type EntityType, object[] KeyValues, bool NeedEmbedding = true) : SmartItem(Table, EntityKey, Utc, Actor);

    public sealed record TimeseriesItem(
        string Table, string EntityKey, DateTime Utc, string? Actor,
        IReadOnlyList<TimeseriesPoint> Points) : SmartItem(Table, EntityKey, Utc, Actor);


}

