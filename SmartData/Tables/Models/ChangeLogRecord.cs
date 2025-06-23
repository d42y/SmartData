namespace SmartData.Tables.Models
{
    public class ChangeLogRecord
    {
        public Guid Id { get; set; }
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public string ChangeBy { get; set; }
        public DateTime ChangeDate { get; set; }
        public string? OriginalData { get; set; }
        public string? NewData { get; set; }
        public string ChangeType { get; set; }
    }
}
