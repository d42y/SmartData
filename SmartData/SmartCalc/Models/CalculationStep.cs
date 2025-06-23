using System.ComponentModel.DataAnnotations;

namespace SmartData.SmartCalc.Models
{
    public class CalculationStep
    {
        [Key]
        public Guid Id { get; set; }
        public Guid CalculationId { get; set; }
        public int StepOrder { get; set; }
        [Required, MaxLength(50)]
        public string OperationType { get; set; } // Math or String
        [Required, MaxLength(1000)]
        public string Expression { get; set; }
        [MaxLength(100)]
        public string ResultVariable { get; set; }
    }
}
