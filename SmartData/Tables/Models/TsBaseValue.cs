namespace SmartData.Tables.Models;

public class TsBaseValue<T>
{
    public Guid Id { get; set; }
    public string TableName { get; set; }
    public string EntityId { get; set; }
    public string PropertyName { get; set; }
    public T Value { get; set; }
    public DateTime StartTime { get; set; }

    public TsBaseValue(T value, DateTime startTime)
    {
        Id = Guid.NewGuid();
        Value = value;
        StartTime = startTime.ToUniversalTime();
    }
}