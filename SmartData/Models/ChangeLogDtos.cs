using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Models
{
    public sealed record ChangeLogQuery(
            string TableName,
            string EntityId,
            DateTime? FromUtc = null,
            DateTime? ToUtc = null,
            string? PropertyName = null,
            string? Actor = null,
            string? ChangeType = null,
            int Skip = 0,
            int Take = 200);

    public sealed record ChangeLogPage(
        int TotalCount,
        IReadOnlyList<ChangeLogRecord> Items);

    public sealed record ChangePropertySummary(
    string PropertyName,
    int Count,
    DateTime FirstUtc,
    DateTime LastUtc,
    string? FirstValue,
    string? LastValue,
    string? LastChangeType,
    string? LastChangedBy
);

    public sealed record ChangeSummary(
        string TableName,
        string? EntityId,
        int TotalChanges,
        DateTime? FirstUtc,
        DateTime? LastUtc,
        IReadOnlyList<string> Actors,
        IReadOnlyList<string> ChangeTypes,
        IReadOnlyList<ChangePropertySummary> Properties
    );
}
