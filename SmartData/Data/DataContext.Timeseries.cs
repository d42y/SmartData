using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartData.Models;

namespace SmartData.Data
{
    public partial class DataContext : DbContext
    {
        public async Task<List<TimeseriesResult>> GetTimeseriesAsync(
            string tableName,
            string entityId,
            string propertyName,
            DateTime start,
            DateTime end,
            CancellationToken ct = default)
        {
            if (!_options.EnableTimeseries)
            {
                _logger?.LogWarning("Timeseries feature is disabled.");
                return new List<TimeseriesResult>(0);
            }

            // 1) Bases up to the end window (they can contribute points within [start,end])
            var baseRows = await Set<TimeseriesBaseValue>()
                .AsNoTracking()
                .Where(b => b.TableName == tableName
                         && b.EntityId == entityId
                         && b.PropertyName == propertyName
                         && b.Timestamp <= end)
                .OrderBy(b => b.Timestamp)
                .ToListAsync(ct);

            if (baseRows.Count == 0)
                return new List<TimeseriesResult>(0);

            var baseIds = baseRows.Select(b => b.Id).ToList();

            // 2) All deltas for those bases
            var deltaRows = await Set<TimeseriesDelta>()
                .AsNoTracking()
                .Where(d => baseIds.Contains(d.BaseValueId))
                .ToListAsync(ct);

            // 3) Index deltas by base id
            var deltasByBase = deltaRows
                .GroupBy(d => d.BaseValueId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var results = new List<TimeseriesResult>(capacity: 1024);

            foreach (var b in baseRows)
            {
                var baseTs = b.Timestamp;

                // Include the base anchor as a datapoint (it represents the first observation of this value)
                if (baseTs >= start && baseTs <= end)
                {
                    results.Add(new TimeseriesResult
                    {
                        Timestamp = baseTs,
                        Value = b.Value
                    });
                }

                if (!deltasByBase.TryGetValue(b.Id, out var rowsForBase))
                    continue;

                foreach (var d in rowsForBase)
                {
                    // Decode raw little-endian int32[] absolute offsets (seconds)
                    var offsets = d.GetOffsets(); // from your updated TimeseriesDelta
                    if (offsets.Count == 0) continue;

                    foreach (var off in offsets)
                    {
                        // Offsets are absolute seconds from the base anchor
                        var ts = baseTs.AddSeconds(off);
                        if (ts >= start && ts <= end)
                        {
                            results.Add(new TimeseriesResult
                            {
                                Timestamp = ts,
                                Value = b.Value
                            });
                        }
                    }
                }
            }

            results.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            return results;
        }

        public async Task<List<TimeseriesResult>> GetInterpolatedTimeseriesAsync(
            string tableName,
            string entityId,
            string propertyName,
            DateTime start,
            DateTime end,
            TimeSpan interval,
            InterpolationMethod method,
            CancellationToken ct = default)
        {
            if (!_options.EnableTimeseries)
            {
                _logger?.LogWarning("Timeseries feature is disabled.");
                return new List<TimeseriesResult>(0);
            }

            var raw = await GetTimeseriesAsync(tableName, entityId, propertyName, start, end, ct);
            var result = new List<TimeseriesResult>();
            if (raw.Count == 0) return result;

            // Iterate on the grid
            for (var t = start; t <= end; t = t.Add(interval))
            {
                if (method == InterpolationMethod.None)
                {
                    var exact = raw.FirstOrDefault(r => r.Timestamp == t);
                    if (exact != null)
                        result.Add(new TimeseriesResult { Timestamp = t, Value = exact.Value });
                    continue;
                }

                // Pick neighbors
                var prev = raw.LastOrDefault(r => r.Timestamp <= t);
                var next = raw.FirstOrDefault(r => r.Timestamp >= t);

                double? value = null;

                switch (method)
                {
                    case InterpolationMethod.Linear:
                        if (prev != null && next != null && prev.Timestamp != next.Timestamp
                            && double.TryParse(prev.Value, out var pv)
                            && double.TryParse(next.Value, out var nv))
                        {
                            var totalMs = (next.Timestamp - prev.Timestamp).TotalMilliseconds;
                            var elapsed = (t - prev.Timestamp).TotalMilliseconds;
                            var frac = elapsed / totalMs;
                            value = pv + (nv - pv) * frac;
                        }
                        else if (prev != null && double.TryParse(prev.Value, out var pvOnly))
                        {
                            value = pvOnly;
                        }
                        else if (next != null && double.TryParse(next.Value, out var nvOnly))
                        {
                            value = nvOnly;
                        }
                        break;

                    case InterpolationMethod.Nearest:
                        if (prev != null && next != null)
                        {
                            var prevDiff = Math.Abs((t - prev.Timestamp).TotalMilliseconds);
                            var nextDiff = Math.Abs((next.Timestamp - t).TotalMilliseconds);
                            value = (prevDiff <= nextDiff)
                                ? (double.TryParse(prev.Value, out var pnv) ? pnv : (double?)null)
                                : (double.TryParse(next.Value, out var nnv) ? nnv : (double?)null);
                        }
                        else if (prev != null && double.TryParse(prev.Value, out var pNearest))
                        {
                            value = pNearest;
                        }
                        else if (next != null && double.TryParse(next.Value, out var nNearest))
                        {
                            value = nNearest;
                        }
                        break;

                    case InterpolationMethod.Previous:
                        if (prev != null && double.TryParse(prev.Value, out var pPrev))
                            value = pPrev;
                        break;

                    case InterpolationMethod.Next:
                        if (next != null && double.TryParse(next.Value, out var nNext))
                            value = nNext;
                        break;
                }

                if (value.HasValue)
                    result.Add(new TimeseriesResult { Timestamp = t, Value = value.Value.ToString("G17") });
            }

            return result;
        }

        // Existing helpers for embeddings…
        private static byte[] FloatArrayToBytes(float[] floats)
        {
            if (floats == null) return Array.Empty<byte>();
            using var stream = new MemoryStream(floats.Length * sizeof(float));
            using var writer = new BinaryWriter(stream);
            foreach (var f in floats) writer.Write(f);
            return stream.ToArray();
        }

        private static float[] BytesToFloatArray(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return Array.Empty<float>();
            if (bytes.Length % sizeof(float) != 0)
                throw new ArgumentException("Byte array length must be a multiple of 4 for float array conversion.");
            var floats = new float[bytes.Length / sizeof(float)];
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);
            for (int i = 0; i < floats.Length; i++)
                floats[i] = reader.ReadSingle();
            return floats;
        }
    }
}
