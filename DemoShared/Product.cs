using SmartData.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemoShared
{
    public class Product
    {
        public int Id { get; set; }
        public string Name { get; set; }

        [Embeddable("Product: ${Name}, Description: ${Description}", Priority = 1)]
        [Timeseries]
        public string Description { get; set; }
        public int CustomerId { get; set; }
        public Customer Customer { get; set; }
    }
}
