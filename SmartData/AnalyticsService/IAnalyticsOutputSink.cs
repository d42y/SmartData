using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.AnalyticsService
{
    /// <summary>
    /// Client-provided sink to persist analytics variables (e.g., into the "Variables" table).
    /// Implement this in your app where the DbContext and client entities exist.
    /// </summary>
    public interface IAnalyticsOutputSink
    {
        Task UpsertVariablesAsync(
            Guid tenantId,
            Guid nodeId,
            IReadOnlyDictionary<string, object> variables,
            CancellationToken ct);
    }
}
