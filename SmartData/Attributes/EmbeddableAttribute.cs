namespace SmartData.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class EmbeddableAttribute : Attribute
    {
        public string Format { get; init; }
        public int Priority { get; init; }


        public EmbeddableAttribute(string format, int priority = 0)
        {
            if ((string.IsNullOrWhiteSpace(format) || string.IsNullOrEmpty(format)) && string.IsNullOrEmpty(Format))
                throw new ArgumentException("Format cannot be null or empty.", nameof(format));
            Format = format;
            Priority = priority;
        }
    }
}
