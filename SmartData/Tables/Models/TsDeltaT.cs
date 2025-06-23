namespace SmartData.Tables.Models
{
    public class TsDeltaT
    {
        public Guid Id { get; set; }
        public Guid BaseValueId { get; set; }
        public byte[] CompressedDeltas { get; set; } = Array.Empty<byte>();
        public int LastTimestamp { get; set; } = -1;
        public int Version { get; set; } = 1;

        public void AddTimestamp(int timestamp)
        {
            var deltas = GetTimeDeltas();
            if (LastTimestamp == -1)
            {
                deltas.Add(0);
            }
            else
            {
                int delta = timestamp - LastTimestamp;
                deltas.Add(delta);
            }
            LastTimestamp = timestamp;
            var compressed = CompressDeltas(deltas);
            if (compressed.Length == 0)
            {
                throw new InvalidOperationException("CompressedDeltas is empty after compression.");
            }
            CompressedDeltas = compressed;
            Version++;
        }

        public List<int> GetTimeDeltas()
        {
            if (CompressedDeltas == null || CompressedDeltas.Length == 0)
                return new List<int>();
            return DecompressDeltas(CompressedDeltas);
        }

        private byte[] CompressDeltas(List<int> deltas)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            foreach (var delta in deltas)
            {
                uint value = (uint)Math.Abs(delta);
                while (value >= 128)
                {
                    writer.Write((byte)(value & 127 | 128));
                    value >>= 7;
                }
                writer.Write((byte)value);
                writer.Write(delta < 0);
            }
            return stream.ToArray();
        }

        private List<int> DecompressDeltas(byte[] compressed)
        {
            var deltas = new List<int>();
            using var stream = new MemoryStream(compressed);
            using var reader = new BinaryReader(stream);
            while (stream.Position < stream.Length)
            {
                uint value = 0;
                int shift = 0;
                byte b;
                do
                {
                    b = reader.ReadByte();
                    value |= (uint)(b & 127) << shift;
                    shift += 7;
                } while ((b & 128) != 0 && stream.Position < stream.Length);
                bool isNegative = reader.ReadBoolean();
                deltas.Add(isNegative ? -(int)value : (int)value);
            }
            return deltas;
        }
    }
}