using SmartData.Attributes;
using System.ComponentModel.DataAnnotations;

namespace DemoIoT
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
