namespace SmartData.Models
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class TrackChangeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property)]
    public class TimeseriesAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class EnsureIntegrityAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = false)]
    public class EmbeddableAttribute : Attribute
    {
        public string Format { get; }
        public int Priority { get; }

        public EmbeddableAttribute(string format, int priority = 0)
        {
            Format = format ?? throw new ArgumentException("Format cannot be null or empty.", nameof(format));
            Priority = priority;
        }
    }
}