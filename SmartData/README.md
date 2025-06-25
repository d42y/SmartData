# SmartData

SmartData is a .NET 8.0 library developed by d42y to simplify data management for Internet of Things (IoT) applications. Built on Entity Framework Core, it offers an intuitive `SdSet<T>` API for relational data, embeddings, and timeseries, with integrated Retrieval-Augmented Generation (RAG) and GPT-based analytics. This enables developers to easily implement GPT-powered question-and-answer (Q&A) systems and gain advanced insights from IoT data, requiring minimal expertise in RAG or vector search technologies.

## Goal

SmartData simplifies data management for IoT applications with an intuitive `SdSet<T>` API for relational data, embeddings, and timeseries. It integrates Retrieval-Augmented Generation (RAG) and GPT-based analytics to enable developers to easily implement GPT-powered question-and-answer (Q&A) systems and advanced data insights on IoT data, with minimal expertise in RAG or vector search technologies.

## Features

- **Relational Data Management**: Perform CRUD operations using the `SdSet<T>` API.
- **Embedding Support**: Generate 384-dimensional embeddings for semantic search, powered by `AllMiniLmL6V2Embedder` and `FaissNet`, enabling RAG for GPT Q&A and analytics.
- **Timeseries Data**: Store and query IoT timeseries data (e.g., sensor readings) with interpolation methods (linear, nearest).
- **GPT-Based Analytics**: Leverage embedded models for advanced data insights, such as trend analysis and anomaly detection.
- **Schema Management**: Automatically create tables for entities, embeddings (`sysEmbeddings`), and timeseries (`sysTsBaseValues`, `sysTsDeltaT`) without migrations, or use migrations for production.
- **Dependency Injection**: Integrates with `Microsoft.Extensions.DependencyInjection` for scoped services.
- **Database Support**: Compatible with SQLite, SQL Server, and other EF Core providers.
- **Embedded Resources**: Includes ONNX models and `FaissNet` for out-of-the-box RAG and analytics.

## Version

0.0.2 (beta)

## License

SmartData is released under the [MIT License](#license).

## Installation

1. **Add NuGet Package**:
   Install the `SmartData` package:

   ```bash
   dotnet add package SmartData --version 0.0.2
   ```

2. **Add Dependencies**:
   Install minimal dependencies for your database provider:

   For SQLite (IoT development):
   ```bash
   dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 9.0.6
   dotnet add package Microsoft.EntityFrameworkCore.Design --version 9.0.6
   dotnet add package Microsoft.Extensions.Logging.Console --version 9.0.6
   ```

   For SQL Server (production):
   ```bash
   dotnet add package Microsoft.EntityFrameworkCore --version 9.0.6
   dotnet add package Microsoft.EntityFrameworkCore.SqlServer --version 9.0.6
   dotnet add package Microsoft.EntityFrameworkCore.Design --version 9.0.6
   dotnet add package Microsoft.Extensions.Logging.Console --version 9.0.6
   ```

3. **Project Reference** (if building from source):
   ```bash
   dotnet add reference ../SmartData/SmartData.csproj
   ```

## Getting Started

### 1. Define Your Entities

Create model classes for IoT or relational data, annotated for embeddings or timeseries.

**IoT Example (Sensor)**:

```csharp
using SmartData.Attributes;
using System.ComponentModel.DataAnnotations;

namespace MyApp
{
    public class Sensor
    {
        [Key]
        public string Id { get; set; }
        [Timeseries]
        public int Temperature { get; set; }
        [Embeddable("Sensor {Id} {Description} is {Temperature} degrees F")]
        public string Description { get; set; }
    }
}
```

**Relational Example (Customer and Product)**:

```csharp
// Customer.cs
using System.ComponentModel.DataAnnotations;

namespace MyApp
{
    public class Customer
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public List<Product> Products { get; set; } = new();
    }
}

// Product.cs
using SmartData.Attributes;
using System.ComponentModel.DataAnnotations;

namespace MyApp
{
    public class Product
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; }
        [Embeddable("Product {Name} owned by customer {CustomerId}")]
        public string Description { get; set; }
        public int CustomerId { get; set; }
        public Customer Customer { get; set; }
    }
}
```

- `[Embeddable]`: Generates embeddings for RAG-based Q&A and analytics.
- `[Timeseries]`: Stores timeseries data (e.g., IoT sensor readings).

### 2. Set Up Dependency Injection

Configure `SmartData` with your database provider. Below are examples for SQLite (no migrations) and SQL Server (with migrations).

#### SQLite (No Migrations)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData;
using SmartData.Configurations;
using SmartData.Extensions;

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
services.AddSqlData<MyDbContext>(builder =>
{
    builder.WithConnectionString("Data Source=IoTData.db")
           .WithLogging(services.BuildServiceProvider().GetRequiredService<ILoggerFactory>())
           .EnableEmbedding()
           .EnableTimeseries();
}, options => options.UseSqlite("Data Source=IoTData.db"));

var serviceProvider = services.BuildServiceProvider();
```

- **No Migrations**: Schema is created automatically, ideal for IoT prototyping.
- **Embedding/Timeseries**: Enabled for RAG Q&A and timeseries analytics.

#### SQL Server (With Migrations)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData;
using SmartData.Configurations;
using SmartData.Extensions;

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
services.AddSqlData<MyDbContext>(builder =>
{
    builder.WithConnectionString("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;")
           .WithMigrationsAssembly("MyApp")
           .WithLogging(loggerFactory)
           .EnableEmbedding();
}, options => options.UseSqlServer("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
    sqlOptions => sqlOptions.MigrationsAssembly("MyApp")));

var serviceProvider = services.BuildServiceProvider();
```

- **Migrations**: Recommended for production to manage schema changes.
- **SQL Server**: Supports robust IoT data storage.

### 3. Define Your DbContext

Create a context class to manage entities:

```csharp
using SmartData.Configurations;
using SmartData.Tables;
using Microsoft.EntityFrameworkCore;

namespace MyApp
{
    public class MyDbContext : SqlDataContext
    {
        public SdSet<Sensor> Sensors { get; set; }
        public SdSet<Customer> Customers { get; set; }
        public SdSet<Product> Products { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Sensor>().ToTable("Sensors");
            modelBuilder.Entity<Sensor>().Property(s => s.Id).IsRequired().HasMaxLength(50);
            modelBuilder.Entity<Sensor>().Property(s => s.Description).HasMaxLength(500);

            modelBuilder.Entity<Customer>().ToTable("Customers");
            modelBuilder.Entity<Customer>().Property(c => c.Name).IsRequired().HasMaxLength(100);
            modelBuilder.Entity<Customer>().Property(c => c.Email).IsRequired().HasMaxLength(255);

            modelBuilder.Entity<Product>().ToTable("Products");
            modelBuilder.Entity<Product>().Property(p => p.Name).IsRequired().HasMaxLength(100);
            modelBuilder.Entity<Product>().Property(p => p.Description).HasMaxLength(500);

            modelBuilder.Entity<Product>()
                .HasOne(p => p.Customer)
                .WithMany(c => c.Products)
                .HasForeignKey(p => p.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
```

### 4. Use SmartData for IoT, RAG, and Analytics

Manage IoT data, perform semantic searches, and enable GPT-based Q&A and analytics:

```csharp
using var scope = serviceProvider.CreateScope();
var sqlData = scope.ServiceProvider.GetRequiredService<SqlData>();
var dbContext = scope.ServiceProvider.GetRequiredService<SmartDataContext>();
var context = scope.ServiceProvider.GetRequiredService<MyDbContext>();

// Create schema (or apply migrations)
await sqlData.MigrateAsync();

// Insert IoT sensor data
var sensors = context.Sensors;
var sensor = new Sensor
{
    Id = "sensor1",
    Temperature = 70,
    Description = "Temperature is 70°F"
};
await sensors.UpsertAsync(sensor);

// Insert relational data
var customers = context.Customers;
var products = context.Products;
var customer = new Customer { Name = "John Doe", Email = "john.doe@example.com" };
await customers.UpsertAsync(customer);
await products.UpsertAsync(new Product
{
    Name = "Laptop",
    Description = "High-performance laptop",
    CustomerId = customer.Id
});

// Semantic search for RAG
var matches = await sensors.SearchEmbeddings("temperature 70", topK = 2);
foreach (var result in matches)
{
    Console.WriteLine($"Sensor Match: Id={result.GetValue<string>("Id")}, Temperature={result.GetValue<int>("Temperature")}°F, Score={result.GetValue<float>("Score")}");
}

// GPT Q&A with ChatService
var chatService = scope.ServiceProvider.GetRequiredService<ChatService>();
var response = await chatService.ChatAsync("What is the current temperature?", sensors, topK = 2);
Console.WriteLine($"Q: What is the current temperature?");
Console.WriteLine($"A: {response}");

// GPT-based analytics (example: trend analysis)
var timeseries = await sensors.GetInterpolatedTimeseriesAsync(
    "sensor1", nameof(Sensor.Temperature), DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, TimeSpan.FromHours(1), InterpolationMethod.Linear);
Console.WriteLine("Temperature Trend (Last 7 Days):");
foreach (var data in timeseries)
{
    Console.WriteLine($"Timestamp: {data.Timestamp}, Temperature: {data.Value}°F");
}
```

### Example Output

```
Tables in database: Sensors, Customers, Products, sysEmbeddings
Inserted sensor sensor1 with temperature 70°F
Inserted customer John Doe (Id: 1)
Inserted product Laptop (Id: 1)
Sensor Match: Id=sensor1, Temperature=70°F, Score=1
Q: What is the current temperature?
A: Based on the context: Sensor sensor1 Temperature is 70°F is 70 degrees F, the answer is: 70°F.
Temperature Trend (Last 7 Days):
Timestamp: 2025-06-11T08:13:00Z, Temperature: 70°F
```

## Configuration

- **Database Providers**: SQLite for IoT prototyping, SQL Server for production, or other EF Core providers.
- **Embedding**: Enabled with `EnableEmbedding()`; uses embedded ONNX models and `FaissNet` for RAG Q&A and analytics.
- **Timeseries**: Enabled with `EnableTimeseries()`; stores IoT data in `sysTsBaseValues` and `sysTsDeltaT`.
- **Schema Management**:
  - **No Migrations**: Automatic schema creation for development (e.g., IoT prototyping).
  - **Migrations**: Recommended for production to manage schema changes.
- **Connection Strings**:
  - SQLite: `Data Source=IoTData.db`
  - SQL Server: `Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;`

## Migrations (Production)

For production, use migrations to manage database schema changes:

1. Add migration:
   ```bash
   dotnet ef migrations add InitialCreate --project MyApp.csproj
   ```

2. Apply migration:
   ```bash
   dotnet ef database update --project MyApp.csproj
   ```

3. Configure `SmartDataContextFactory` for migrations:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using SmartData;
using SmartData.Configurations;
using SmartData.Extensions;

namespace MyApp
{
    public class SmartDataContextFactory : IDesignTimeDbContextFactory<SmartDataContext>
    {
        public SmartDataContext CreateDbContext(string[] args)
        {
            var services = new ServiceCollection();
            services.AddSqlData<MyDbContext>(builder =>
            {
                builder.WithConnectionString("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;")
                       .WithMigrationsAssembly("MyApp");
            }, options => options.UseSqlServer("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
                sqlOptions => sqlOptions.MigrationsAssembly("MyApp")));

            var serviceProvider = services.BuildServiceProvider();
            var appDbContext = serviceProvider.GetRequiredService<MyDbContext>();
            var smartDataOptions = serviceProvider.GetRequiredService<SmartDataOptions>();

            var optionsBuilder = new DbContextOptionsBuilder<SmartDataContext>();
            optionsBuilder.UseSqlServer("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
                sqlOptions => sqlOptions.MigrationsAssembly("MyApp"));

            return new SmartDataContext(optionsBuilder.Options, smartDataOptions, migrationsAssembly: "MyApp", sqlDataContext: appDbContext);
        }
    }
}
```

## Dependencies

User projects require minimal dependencies, as `SmartData` embeds additional libraries (e.g., `FaissNet`, `Microsoft.ML.OnnxRuntime`) for RAG and analytics.

For SQLite:
- .NET 8.0
- [Microsoft.EntityFrameworkCore.Sqlite](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite/9.0.6) (9.0.6)
- [Microsoft.EntityFrameworkCore.Design](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Design/9.0.6) (9.0.6)
- [Microsoft.Extensions.Logging.Console](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Console/9.0.6) (9.0.6)

For SQL Server:
- .NET 8.0
- [Microsoft.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore/9.0.6) (9.0.6)
- [Microsoft.EntityFrameworkCore.SqlServer](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/9.0.6) (9.0.6)
- [Microsoft.EntityFrameworkCore.Design](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Design/9.0.6) (9.0.6)
- [Microsoft.Extensions.Logging.Console](https://www.nuget.org/packages/Microsoft.Extensions.Logging.Console/9.0.6) (9.0.6)

## Building from Source

1. Clone the repository:
   ```bash
   git clone https://github.com/d42y/SmartData.git
   cd SmartData
   ```

2. Build the solution:
   ```bash
   dotnet build
   ```

3. Run a demo project:
   ```bash
   cd DemoSqlServer
   dotnet run
   ```

## Contributing

Contributions are welcome! Submit issues or pull requests to the [GitHub repository](https://github.com/d42y/SmartData). Follow the [code of conduct](CODE_OF_CONDUCT.md).

## License

MIT License

Copyright (c) 2025 d42y

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.