using SmartData.Attributes;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemoEmbedding
{
    public class Sensor
    {
        [Key]
        public string Id { get; set; }

        public int Temperature { get; set; }

        [Embeddable("Sensor {0}")]
        public string Description { get; set; }
    }
}
