using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Tables
{
    public class EmbeddingRecord
    {
        public Guid Id { get; set; }
        public object EntityId { get; set; }
        public float[] Embedding { get; set; }
        public string TableName { get; set; }
    }
}
