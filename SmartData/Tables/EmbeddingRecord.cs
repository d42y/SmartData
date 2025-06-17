namespace SmartData.Tables
{
    public class EmbeddingRecord
    {
        public Guid Id { get; set; }
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public float[] Embedding { get; set; }
    }
}
