using SmartData.Attributes;

namespace DemoShared
{
    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Embeddable("Customer: ${Name}, Email: ${Email}", Priority = 1)]
        [Timeseries]
        public string Email { get; set; }
        public ICollection<Product> Products { get; set; } = new List<Product>();
    }
}
