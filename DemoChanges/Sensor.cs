using SmartData.Attributes;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemoChanges
{
    public class Sensor
    {
        [Key] public string Id { get; set; }

        [TrackChange]
        [EnsureIntegrity]
        [Timeseries]
        public int Temperature { get; set; }

        [Embeddable("Sensor {Id} {Description} is {Temperature} degrees F")]
        public string Description { get; set; }
    }
}
