using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SmartData.Tables.Models
{
    public class IntegrityLogRecord
    {
        public Guid Id { get; set; }
        public string TableName { get; set; }
        public string EntityId { get; set; }
        public string PropertyName { get; set; }
        public string DataHash { get; set; }
        public string PreviousHash { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
