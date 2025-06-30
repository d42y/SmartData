using System.Text.Json;

namespace SmartData.Models
{
    public enum InterpolationMethod
    {
        None,
        Linear,
        Nearest,
        Previous,
        Next
    }

    public class TimeseriesResult
    {
        public DateTime Timestamp { get; set; }
        public string Value { get; set; }
    }

    public class QueryResult
    {
        public Dictionary<string, object> Data { get; }
        public QueryResult(Dictionary<string, object> data) => Data = data ?? throw new ArgumentNullException(nameof(data));
        public T GetValue<T>(string column) => Data.TryGetValue(column, out var value) && value is T t ? t : default;
        public string ToJson() => JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
        public T MapTo<T>() where T : new()
        {
            var result = new T();
            foreach (var prop in typeof(T).GetProperties())
                if (Data.ContainsKey(prop.Name))
                    prop.SetValue(result, Data[prop.Name]);
            return result;
        }
    }

    public class EmbeddingRecord
    {
        public Guid Id { get; set; }
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public float[] Embedding { get; set; }
    }

    public class TimeseriesBaseValue
    {
        public Guid Id { get; set; }
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public string PropertyName { get; set; }
        public string Value { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class TimeseriesDelta
    {
        public Guid Id { get; set; }
        public Guid BaseValueId { get; set; }
        public byte[] Deltas { get; set; } = Array.Empty<byte>();
        public int LastTimestamp { get; set; } = -1;
        public int Version { get; set; } = 1;

        public void AddTimestamp(int timestamp)
        {
            var deltas = GetDeltas();
            deltas.Add(LastTimestamp == -1 ? 0 : timestamp - LastTimestamp);
            LastTimestamp = timestamp;
            Deltas = CompressDeltas(deltas);
            Version++;
        }

        public List<int> GetDeltas()
        {
            if (Deltas == null || Deltas.Length == 0) return new List<int>();
            using var stream = new MemoryStream(Deltas);
            using var reader = new BinaryReader(stream);
            var deltas = new List<int>();
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
    }

    public class ChangeLogRecord
    {
        public Guid Id { get; set; }
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public string ChangedBy { get; set; }
        public string PropertyName { get; set; }
        public DateTime ChangedAt { get; set; }
        public string OriginalValue { get; set; }
        public string NewValue { get; set; }
        public string ChangeType { get; set; }
    }

    public class IntegrityLogRecord
    {
        public Guid Id { get; set; }
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public string PropertyName { get; set; }
        public string Hash { get; set; }
        public string PreviousHash { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class Analytics
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Value { get; set; }
        public int Interval { get; set; }
        public DateTime? LastRun { get; set; }
        public bool Embeddable { get; set; }
        public string Status { get; set; }
    }

    public class AnalyticsStep
    {
        public Guid Id { get; set; }
        public Guid AnalyticsId { get; set; }
        public int Order { get; set; }
        public string Operation { get; set; }
        public string Expression { get; set; }
        public string ResultVariable { get; set; }
        public int MaxLoop { get; set; } = 10; // Default MaxLoop for Condition steps
    }

    public class DataIntegrityException : Exception
    {
        public string TableName { get; }
        public string EntityId { get; }
        public string PropertyName { get; }
        public string ExpectedHash { get; }
        public string ActualHash { get; }

        public DataIntegrityException(string tableName, string entityId, string propertyName, string expectedHash, string actualHash, string message)
            : base(message)
        {
            TableName = tableName;
            EntityId = entityId;
            PropertyName = propertyName;
            ExpectedHash = expectedHash;
            ActualHash = actualHash;
        }
    }

    public enum EntityOperation
    {
        Insert,
        Update,
        Delete
    }

    public class EntityChangeEvent
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public EntityOperation Operation { get; set; }
        public Dictionary<string, (object OldValue, object NewValue)> ChangedProperties { get; set; } // For updates
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}