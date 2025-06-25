using Microsoft.EntityFrameworkCore;
using SmartData.Configurations;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemoIoT
{
    public class AppDbContext : SqlDataContext
    {
        public SdSet<Sensor> Sensors { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider)
        {
        }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Sensor>().ToTable("Sensors");
            modelBuilder.Entity<Sensor>().Property(s => s.Id).IsRequired().HasMaxLength(50);
            modelBuilder.Entity<Sensor>().Property(s => s.Description).HasMaxLength(500);
        }
    }
}
