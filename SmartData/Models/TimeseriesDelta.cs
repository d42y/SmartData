namespace SmartData.Models
{
    /// <summary>
    /// TimeseriesDelta stores offsets (in seconds) from the base anchor timestamp.
    /// Payload format: raw little-endian int32[], packed back-to-back in <see cref="Deltas"/>.
    /// </summary>
    public class TimeseriesDelta
    {
        public Guid Id { get; set; }
        public Guid BaseValueId { get; set; }

        /// <summary>
        /// Raw payload: concatenated 4-byte little-endian Int32 offsets (seconds from base).
        /// </summary>
        public byte[] Deltas { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// The last (max) offset in seconds contained in <see cref="Deltas"/>.
        /// </summary>
        public int LastTimestamp { get; set; } = -1;

        /// <summary>
        /// Optimistic concurrency/versioning field (increment when mutating).
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// Add a single ABSOLUTE offset (seconds from base). Offsets should be positive and non-decreasing.
        /// </summary>
        public void AddTimestamp(int offsetSecondsFromBase)
        {
            if (offsetSecondsFromBase <= 0)
                throw new ArgumentOutOfRangeException(nameof(offsetSecondsFromBase), "Offset must be > 0 seconds.");

            var current = GetOffsetsMutable();
            if (current.Count > 0 && offsetSecondsFromBase < current[^1])
                throw new InvalidOperationException("Offsets must be appended in non-decreasing order.");

            current.Add(offsetSecondsFromBase);
            Deltas = PackOffsets(current);
            LastTimestamp = offsetSecondsFromBase;
            Version++;
        }

        /// <summary>
        /// Replace the payload with the given ABSOLUTE offsets (seconds from base).
        /// </summary>
        public void SetOffsets(IReadOnlyList<int> offsets)
        {
            if (offsets is null) throw new ArgumentNullException(nameof(offsets));
            if (offsets.Count > 0)
            {
                // must be > 0 and non-decreasing
                var prev = 0;
                for (int i = 0; i < offsets.Count; i++)
                {
                    var v = offsets[i];
                    if (v <= 0) throw new ArgumentOutOfRangeException(nameof(offsets), "All offsets must be > 0.");
                    if (i > 0 && v < prev) throw new InvalidOperationException("Offsets must be non-decreasing.");
                    prev = v;
                }
                LastTimestamp = offsets[^1];
            }
            else
            {
                LastTimestamp = -1;
            }

            Deltas = PackOffsets(offsets);
            Version++;
        }

        /// <summary>
        /// Append a batch of ABSOLUTE offsets (seconds from base). Must be >= current LastTimestamp.
        /// </summary>
        public void AppendOffsets(IReadOnlyList<int> moreOffsets)
        {
            if (moreOffsets is null) throw new ArgumentNullException(nameof(moreOffsets));
            if (moreOffsets.Count == 0) return;

            var current = GetOffsetsMutable();
            var startCheck = current.Count > 0 ? current[^1] : 0;

            foreach (var v in moreOffsets)
            {
                if (v <= 0) throw new ArgumentOutOfRangeException(nameof(moreOffsets), "All offsets must be > 0.");
                if (v < startCheck) throw new InvalidOperationException("Offsets must be appended in non-decreasing order.");
                current.Add(v);
                startCheck = v;
            }

            Deltas = PackOffsets(current);
            LastTimestamp = current[^1];
            Version++;
        }

        /// <summary>
        /// Decode the payload as ABSOLUTE offsets (seconds from base).
        /// </summary>
        public IReadOnlyList<int> GetOffsets()
        {
            if (Deltas is null || Deltas.Length == 0)
                return Array.Empty<int>();

            if ((Deltas.Length & 3) != 0)
                throw new InvalidDataException($"Delta payload length {Deltas.Length} is not a multiple of 4.");

            var count = Deltas.Length / 4;
            var values = new int[count];
            Buffer.BlockCopy(Deltas, 0, values, 0, Deltas.Length);
            return values;
        }

        // --- helpers ---

        private List<int> GetOffsetsMutable()
        {
            if (Deltas is null || Deltas.Length == 0)
                return new List<int>();

            if ((Deltas.Length & 3) != 0)
                throw new InvalidDataException($"Delta payload length {Deltas.Length} is not a multiple of 4.");

            var count = Deltas.Length / 4;
            var values = new int[count];
            Buffer.BlockCopy(Deltas, 0, values, 0, Deltas.Length);
            return new List<int>(values);
        }

        private static byte[] PackOffsets(IReadOnlyList<int> offsets)
        {
            if (offsets is null || offsets.Count == 0) return Array.Empty<byte>();

            var buf = new byte[offsets.Count * 4];
            // Little-endian int32s back-to-back
            var span = new Span<byte>(buf);
            for (int i = 0; i < offsets.Count; i++)
            {
                if (!BitConverter.TryWriteBytes(span.Slice(i * 4, 4), offsets[i]))
                    throw new InvalidOperationException("Failed to write offset to payload.");
            }
            return buf;
        }
    }
}
