using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SmartData.Core.Queue
{
    public sealed record TrackedChange(string Property, string? Original, string? New, string ChangeType);
    public sealed record IntegrityProp(string Property, string CurrentValue);
    public sealed record TimeseriesProp(string Property, string Value);

    // One queued unit for a single entity row change
    public sealed record SmartWorkItem(
        string Table,
        string EntityKey,                // e.g. "TenantId|NodeId"
        string[] KeyNames,               // ["TenantId","NodeId"]
        object?[] KeyValues,             // [Guid, Guid]
        Type EntityType,
        EntityState State,
        string ChangedBy,
        DateTime TimestampUtc,
        IReadOnlyList<TrackedChange> Changes,
        IReadOnlyList<IntegrityProp> Integrity,
        IReadOnlyList<TimeseriesProp> Timeseries,
        bool NeedEmbedding               // compute embeddings after commit
    );
}
