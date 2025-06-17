using DemoShared;
using Microsoft.EntityFrameworkCore;
using SmartData.Configurations;

namespace DemoSqlServer
{
    public class AppDbContext : SqlDataContext
    {
        public DataSet<Product> Products { get; set; }
        public DataSet<Customer> Customers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Configure Product
            modelBuilder.Entity<Product>().ToTable("Products"); // Explicitly map to Products
            modelBuilder.Entity<Product>().Property(p => p.Name).IsRequired().HasMaxLength(100);
            modelBuilder.Entity<Product>().Property(p => p.Description).HasMaxLength(500);

            // Configure Customer
            modelBuilder.Entity<Customer>().ToTable("Customers"); // Explicitly map to Customers
            modelBuilder.Entity<Customer>().Property(c => c.Name).IsRequired().HasMaxLength(100);
            modelBuilder.Entity<Customer>().Property(c => c.Email).IsRequired().HasMaxLength(255);

            // Configure one-to-many relationship with navigation property
            modelBuilder.Entity<Product>()
                .HasOne(p => p.Customer)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}