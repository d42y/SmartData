using DemoShared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData;
using SmartData.Configurations;
using SmartData.Extensions;

namespace DemoSqlServer
{
    public class AppDbContext : SqlDataContext
    {
        public SdSet<Product> Products { get; set; }
        public SdSet<Customer> Customers { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options, IServiceProvider serviceProvider)
            : base(options, serviceProvider)
        {
        }

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

    public class SmartDataContextFactory : IDesignTimeDbContextFactory<SmartDataContext>
    {
        public SmartDataContext CreateDbContext(string[] args)
        {
            var services = new ServiceCollection();
            services.AddSqlData<AppDbContext>(builder =>
            {
                builder.WithConnectionString("Server=localhost;Database=SmartDataDemo;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;")
                       .WithMigrationsAssembly("DemoSqlServer");
            }, options => options.UseSqlServer("Server=localhost;Database=SmartDataDemo;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoSqlServer")));

            var serviceProvider = services.BuildServiceProvider();
            var appDbContext = serviceProvider.GetRequiredService<AppDbContext>();
            var sqlData = serviceProvider.GetRequiredService<SqlData>();


            var optionsBuilder = new DbContextOptionsBuilder<SmartDataContext>();
            optionsBuilder.UseSqlServer("Server=localhost;Database=SmartDataDemo;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoSqlServer"));

            var smartDataOptions = serviceProvider.GetRequiredService<SmartDataOptions>(); // NEW: Resolve SmartDataOptions
            var dbContext = new SmartDataContext(optionsBuilder.Options, smartDataOptions, migrationsAssembly: "DemoSqlite", sqlDataContext: appDbContext);

            return dbContext;
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.AddConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                });
            });
            var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
            services.AddSqlData<AppDbContext>(builder =>
            {
                builder.WithConnectionString("Server=localhost;Database=SmartDataDemo;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;")
                       .WithMigrationsAssembly("DemoSqlServer")
                       .WithLogging(loggerFactory);
            }, options => options.UseSqlServer("Server=localhost;Database=SmartDataDemo;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
                sqlOptions => sqlOptions.MigrationsAssembly("DemoSqlServer")));

            var serviceProvider = services.BuildServiceProvider();

            using (var scope = serviceProvider.CreateScope())
            {
                var sqlData = scope.ServiceProvider.GetRequiredService<SqlData>();
                //var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
                var appDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                await sqlData.MigrateAsync();

                await RunDemoAsync(appDbContext);
            }
        }

        static async Task RunDemoAsync(AppDbContext appDbContext)
        {
            var products = appDbContext.Products;
            var customers = appDbContext.Customers;
            

            Console.WriteLine("Inserting customers...");
            var customer1 = new Customer { Name = "John Doe", Email = "john.doe@example.com" };
            var customer2 = new Customer { Name = "Jane Smith", Email = "jane.smith@example.com" };
            await customers.InsertAsync(new[] { customer1, customer2 });
            Console.WriteLine($"Inserted customers: {customer1.Name} (Id: {customer1.Id}), {customer2.Name} (Id: {customer2.Id})");

            Console.WriteLine("\nInserting products...");
            var productsToInsert = new List<Product>
            {
                new Product { Name = "Laptop", Description = "High-performance laptop", CustomerId = customer1.Id },
                new Product { Name = "Tablet", Description = "Portable tablet device", CustomerId = customer1.Id },
                new Product { Name = "Smartphone", Description = "Latest smartphone model", CustomerId = customer2.Id }
            };
            await products.InsertAsync(productsToInsert);
            foreach (var product in productsToInsert)
            {
                //var embedding = await products.GetEmbeddingAsync(product.Id);
                //Console.WriteLine($"Product inserted: {product.Name}, Id: {product.Id}, CustomerId: {product.CustomerId}, Embedding length: {embedding?.Length ?? 0}");
            }

            Console.WriteLine("\nRetrieving customer with products using navigation property...");
            var retrievedCustomer = await customers.FindByIdAsync(customer1.Id);
            Console.WriteLine($"Retrieved: {retrievedCustomer?.Name}, Email: {retrievedCustomer?.Email}");
            Console.WriteLine($"Customer {retrievedCustomer?.Name} has {retrievedCustomer?.Products.Count} products:");
            foreach (var product in retrievedCustomer?.Products ?? new List<Product>())
            {
                Console.WriteLine($"  - {product.Name} (Id: {product.Id})");
            }

            Console.WriteLine("\nUsing ExecuteSqlQueryAsync to list products for customer...");
            var productSql = "SELECT * FROM Products WHERE CustomerId = {0}";
            //var customerProducts = await products.ExecuteSqlQueryAsync(productSql, customer1.Id);
            //Console.WriteLine($"Customer {customer1.Name} has {customerProducts.Count} products (via ExecuteSqlQueryAsync):");
            //foreach (var product in customerProducts)
            //{
            //    Console.WriteLine($"  - {product.Name} (Id: {product.Id})");
            //}

            Console.WriteLine("\nRunning SQL query with JOIN using Query extension...");
            var sqlQuery = @"
                SELECT c.Id AS CustomerId, c.Name AS CustomerName, p.Id AS ProductId, p.Name AS ProductName
                FROM Customers c
                LEFT JOIN Products p ON c.Id = p.CustomerId
                ORDER BY c.Name, p.Name";
            var results = await appDbContext.ExecuteSqlQueryAsync(sqlQuery);
            Console.WriteLine("Query results as JSON:");
            foreach (var result in results)
            {
                Console.WriteLine(result.ToJson());
            }

            Console.WriteLine("\nUpdating product...");
            var productToUpdate = productsToInsert[0];
            productToUpdate.Description = "Updated high-performance laptop";
            var updated = await products.UpdateAsync(productToUpdate);
            Console.WriteLine($"Update successful: {updated}");
            //var updatedEmbedding = await products.GetEmbeddingAsync(productToUpdate.Id);
            //Console.WriteLine($"Updated embedding length: {updatedEmbedding?.Length ?? 0}");

            Console.WriteLine("\nUpdating customer...");
            customer1.Email = "john.doe@newdomain.com";
            var customerUpdated = await customers.UpdateAsync(customer1);
            Console.WriteLine($"Customer update successful: {customerUpdated}");

            Console.WriteLine("\nPress 'D' to delete all data or 'Esc' to exit...");
            while (true)
            {
                var key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.D)
                {
                    Console.WriteLine("\nDeleting all products...");
                    var deletedProductCount = await products.DeleteAllAsync();
                    Console.WriteLine($"Deleted {deletedProductCount} products.");

                    Console.WriteLine("Deleting all customers...");
                    var deletedCustomerCount = await customers.DeleteAllAsync();
                    Console.WriteLine($"Deleted {deletedCustomerCount} customers.");
                    break;
                }
                else if (key == ConsoleKey.Escape)
                {
                    Console.WriteLine("Exiting without deleting data.");
                    break;
                }
            }

            Console.WriteLine("Demo completed. Press any key to exit.");
            Console.ReadKey();
        }
    }
}