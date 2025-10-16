using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartData.Models;

namespace SmartData.Data
{
    public partial class DataContext : DbContext
    {


        /// <summary>
        /// Convenience: load the latest integrity snapshot (latest hash per property) for an entity.
        /// </summary>
        public async Task<Dictionary<string, (string Hash, DateTime Utc)>> GetLatestIntegritySnapshotAsync(
            string tableName,
            string entityId,
            CancellationToken ct = default)
        {
            if (!_options.EnableIntegrityVerification)
                return new Dictionary<string, (string, DateTime)>(StringComparer.OrdinalIgnoreCase);

            // Latest row per (Table, Entity, Property)
            var rows = await IntegrityLogRecords.AsNoTracking()
                .Where(x => x.TableName == tableName && x.EntityId == entityId)
                .GroupBy(x => x.PropertyName)
                .Select(g => g.OrderByDescending(r => r.Timestamp).First())
                .ToListAsync(ct);

            return rows.ToDictionary(
                r => r.PropertyName,
                r => (r.Hash, r.Timestamp),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Verify integrity for arbitrary current values: we recompute the SHA256 (Base64) of the
        /// normalized value and compare it to the latest stored hash per property.
        /// Only properties present in <paramref name="currentValues"/> are checked.
        /// </summary>
        public async Task<IntegrityCheckResult> CheckIntegrityAsync(
            string tableName,
            string entityId,
            IReadOnlyDictionary<string, object?> currentValues,
            CancellationToken ct = default)
        {
            var latest = await GetLatestIntegritySnapshotAsync(tableName, entityId, ct);

            // Build statuses for each provided property
            var props = new List<IntegrityPropertyStatus>(currentValues.Count);
            foreach (var kvp in currentValues)
            {
                var prop = kvp.Key;
                var normalized = NormalizeForHash(kvp.Value);
                var recomputed = Sha256Base64(normalized);

                if (latest.TryGetValue(prop, out var row))
                {
                    props.Add(new IntegrityPropertyStatus(
                        Property: prop,
                        LatestHash: row.Hash,
                        RecomputedHash: recomputed,
                        LatestTimestampUtc: row.Utc,
                        Matches: string.Equals(row.Hash, recomputed, StringComparison.Ordinal)
                    ));
                }
                else
                {
                    // No reference hash logged yet for this property
                    props.Add(new IntegrityPropertyStatus(
                        Property: prop,
                        LatestHash: null,
                        RecomputedHash: recomputed,
                        LatestTimestampUtc: null,
                        Matches: null
                    ));
                }
            }

            return new IntegrityCheckResult(tableName, entityId, props);
        }

        /// <summary>
        /// Optional chain verification for a property: if your pipeline begins populating
        /// PreviousHash in __sysIntegrityLog, this verifies the hash chain (latest back to earliest).
        /// If PreviousHash is not populated in your data, this will simply return true.
        /// </summary>
        public async Task<bool> VerifyIntegrityChainAsync(
            string tableName,
            string entityId,
            string propertyName,
            CancellationToken ct = default)
        {
            var rows = await IntegrityLogRecords.AsNoTracking()
                .Where(x => x.TableName == tableName && x.EntityId == entityId && x.PropertyName == propertyName)
                .OrderBy(r => r.Timestamp)
                .ToListAsync(ct);

            if (rows.Count <= 1) return true; // nothing to chain-check

            // If your writer is not populating PreviousHash yet, skip chain validation.
            if (rows.All(r => string.IsNullOrEmpty(r.PreviousHash))) return true;

            // Validate that each row's PreviousHash equals the prior row's Hash
            for (int i = 1; i < rows.Count; i++)
            {
                if (!string.Equals(rows[i].PreviousHash, rows[i - 1].Hash, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Compare a current (already stringified) value to the latest logged hash for a property.
        /// Returns true if integrity verification is disabled, if no hash has been logged yet,
        /// or if the hashes match; otherwise false.
        /// </summary>
        public async Task<bool> VerifyIntegrityLatestAsync(
            string tableName,
            string entityId,
            string propertyName,
            string currentValue,
            CancellationToken ct = default)
        {
            if (!_options.EnableIntegrityVerification) return true;

            var latest = await IntegrityLogRecords.AsNoTracking()
                .Where(l => l.TableName == tableName &&
                            l.EntityId == entityId &&
                            l.PropertyName == propertyName)
                .OrderByDescending(l => l.Timestamp)
                .Select(l => l.Hash)
                .FirstOrDefaultAsync(ct);

            // If we have no reference hash yet, treat as "no discrepancy".
            if (latest is null) return true;

            var currentHash = Sha256Base64(currentValue ?? string.Empty);
            return string.Equals(latest, currentHash, StringComparison.Ordinal);

        }
        /// <summary>
        /// Convenience overload: takes any CLR value, normalizes it (stable string / JSON),
        /// hashes it, and compares to the latest logged hash.
        /// </summary>
        public Task<bool> VerifyIntegrityLatestAsync(
            string tableName,
            string entityId,
            string propertyName,
            object? currentValue,
            CancellationToken ct = default)
            => VerifyIntegrityLatestAsync(
                tableName,
                entityId,
                propertyName,
                NormalizeForHash(currentValue),
                ct);
        
        /// <summary>
        /// Convenience overload for numeric values: invariant-culture stringification.
        /// </summary>
        public Task<bool> VerifyIntegrityLatestAsync(
            string tableName,
            string entityId,
            string propertyName,
            double currentValue,
            CancellationToken ct = default)
            => VerifyIntegrityLatestAsync(
                tableName,
                entityId,
                propertyName,
                currentValue.ToString(CultureInfo.InvariantCulture),
                ct);
    
        // ===================== Integrity: value normalization + hashing =====================

        private static string NormalizeForHash(object? value)
        {
            // Match the "normalization/minification" approach used in your interceptor:
            // - null -> empty string
            // - primitives -> invariant string
            // - complex -> stable JSON (ordered props, no whitespace)
            if (value is null) return string.Empty;

            switch (value)
            {
                case string s:
                    return s;
                case IFormattable f:
                    return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                default:
                    // Serialize with stable options
                    var opts = new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = false
                    };
                    // NOTE: If you rely on property order for hashing, consider a deterministic converter.
                    return System.Text.Json.JsonSerializer.Serialize(value, opts);
            }
        }

        private static string Sha256Base64(string data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(data ?? string.Empty);
            return Convert.ToBase64String(sha.ComputeHash(bytes));
        }
    }
}
