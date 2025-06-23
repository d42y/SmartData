using System.ComponentModel.DataAnnotations;

namespace SmartData.SmartCalc.Models
{
    public class Calculation
    {
        [Key]
        public Guid Id { get; set; }
        [Required, MaxLength(100)]
        public string Name { get; set; }
        [MaxLength(4000)]
        public string Value { get; set; }
        public int Interval { get; set; } // Negative: OnChange, 0: Manual, Positive: Timer (seconds)
        public DateTime? LastRun { get; set; }
        public bool Embeddable { get; set; }

        public void SetInterval(int intervalSeconds)
        {
            Interval = intervalSeconds;
        }
    }
}
