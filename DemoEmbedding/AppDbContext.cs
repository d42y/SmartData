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
        public SdSet<Sensor> Sensors { get; private set; }

        public AppDbContext(DbContextOptions<AppDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider)
        {
        }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Sensor>().ToTable("Sensors");
        }
    }
}
