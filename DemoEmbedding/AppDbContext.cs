using Microsoft.EntityFrameworkCore;
using SmartData;
using SmartData.Configurations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemoEmbedding
{
    public class AppDbContext : SqlDataContext
    {
        public DataSet<Sensor> Sensors { get; private set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Sensor>().ToTable("Sensors");
        }
    }
}
