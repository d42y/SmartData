using Microsoft.EntityFrameworkCore;
using SmartData.Configurations;
using DemoShared;

namespace DemoSqlite
{
    public class AppDbContext : SqlDataContext
    {
        public SdSet<Product> Products { get; set; }
        public SdSet<Customer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>().ToTable("Products");
            modelBuilder.Entity<Product>().Property(p => p.Name).IsRequired().HasMaxLength(100);
            modelBuilder.Entity<Product>().Property(p => p.Description).HasMaxLength(500);

            modelBuilder.Entity<Customer>().ToTable("Customers");
            modelBuilder.Entity<Customer>().Property(c => c.Name).IsRequired().HasMaxLength(100);
            modelBuilder.Entity<Customer>().Property(c => c.Email).IsRequired().HasMaxLength(255);

            modelBuilder.Entity<Product>()
                .HasOne(p => p.Customer)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}