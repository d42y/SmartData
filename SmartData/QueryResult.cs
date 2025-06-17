using System.Text.Json;

namespace SmartData
{
    public class QueryResult
    {
        public Dictionary<string, object> Data { get; }

        public QueryResult(Dictionary<string, object> data)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public T GetValue<T>(string columnName)
        {
            if (!Data.ContainsKey(columnName))
                throw new KeyNotFoundException($"Column '{columnName}' not found.");
            return Data[columnName] is T value ? value : default;
        }

        public string ToJson()
        {
            return JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
        }

        public T MapTo<T>() where T : new()
        {
            var result = new T();
            foreach (var property in typeof(T).GetProperties())
            {
                if (Data.ContainsKey(property.Name))
                {
                    property.SetValue(result, Data[property.Name]);
                }
            }
            return result;
        }
    }
}