using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class EmbeddableAttribute : Attribute
    {
        public string Format { get; }
        public int Priority { get; }

        public EmbeddableAttribute(string format, int priority = 0)
        {
            Format = format ?? throw new ArgumentNullException(nameof(format));
            Priority = priority;
        }
    }
}
