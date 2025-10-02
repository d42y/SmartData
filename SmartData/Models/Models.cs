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
        public Guid TenantId { get; set; }
        public bool Enabled {  get; set; }
        public string OutputMode { get; set; } = "Memory"; // NEW: "Memory" | "Table"
        public string? OutputTable { get; set; }       //e.g., "Variables"
        public DateTime? NextRun { get; set; }         //scheduler convenience
        public DateTime? LastRun { get; set; }
        public int Interval { get; set; } //seconds
        public string Name { get; set; }
        public string Value { get; set; }
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