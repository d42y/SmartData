namespace SmartData.Exceptions
{
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
}
