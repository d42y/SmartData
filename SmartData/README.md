# SmartData

SmartData is a .NET 8.0 library developed by d42y to simplify data management for Internet of Things (IoT) and smart building applications. Built on Entity Framework Core, it provides an intuitive `SdSet<T>` API for relational data, vector embeddings, timeseries, change tracking, data integrity, and advanced analytics. With integrated Retrieval-Augmented Generation (RAG) and analytics capabilities, SmartData enables developers to implement GPT-powered question-and-answer (Q&A) systems and gain advanced insights from IoT data with minimal expertise in RAG or vector search technologies.

## Goal

SmartData simplifies data management for IoT and smart building applications by offering a unified `SdSet<T>` API for relational data, vector embeddings, timeseries, change tracking, data integrity, and analytics. It integrates Retrieval-Augmented Generation (RAG) and advanced analytics to enable developers to build GPT-powered Q&A systems and derive actionable insights from IoT data, such as sensor readings, trend analysis, and anomaly detection, with minimal setup and expertise.

## Features

- **Relational Data Management**: Perform CRUD operations using the `SdSet<T>` API, built on Entity Framework Core, with support for multiple SQL database providers (e.g., SQLite, SQL Server).
- **Vector Embeddings**: Generate 384-dimensional embeddings for semantic search using `AllMiniLmL6V2Embedder` and `FaissNet`, enabling RAG for GPT-powered Q&A and analytics.
- **Timeseries Data**: Store and query IoT timeseries data (e.g., sensor readings) with support for interpolation methods (None, Linear, Nearest, Previous, Next).
- **Change Tracking**: Log entity changes (insert, update, delete) with detailed audit trails in the `sysChangeLog` table.
- **Data Integrity**: Ensure data consistency with SHA256-based hash chains stored in the `sysIntegrityLog` table.
- **Advanced Analytics**: Execute complex analytics workflows using SQL queries, C# scripts, conditional logic, and timeseries operations via the `SmartAnalyticsService`, with support for event-driven and time-based triggers.
- **Schema Management**: Automatically create tables for entities, embeddings (`sysEmbeddings`), timeseries (`sysTimeseriesBaseValues`, `sysTimeseriesDeltas`), change logs (`sysChangeLog`), integrity logs (`sysIntegrityLog`), and analytics (`sysAnalytics`, `sysAnalyticsSteps`) without migrations, or use migrations for production environments.
- **Dependency Injection**: Seamlessly integrates with `Microsoft.Extensions.DependencyInjection` for scoped services.
- **Database Support**: Compatible with SQLite, SQL Server, and other EF Core providers.
- **Embedded Resources**: Includes ONNX models and `FaissNet` for out-of-the-box RAG and analytics capabilities.

## Version

0.0.3 (beta)

## License

SmartData is released under the [MIT License](#license).

## Installation

1. **Add NuGet Package**:
   Install the `SmartData` package:

   ```bash
   dotnet add package SmartData --version 0.0.3
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

Create model classes for IoT or relational data, using attributes to enable embeddings, timeseries, change tracking, or data integrity.

**IoT Example (Sensor)**:

```csharp
using SmartData.Models;
using System.ComponentModel.DataAnnotations;

namespace MyApp
{
    public class Sensor
    {
        [Key]
        public string Id { get; set; }
        [Timeseries]
        [TrackChange]
        [EnsureIntegrity]
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
        [TrackChange]
        public string Name { get; set; }
        public string Email { get; set; }
        public List<Product> Products { get; set; } = new();
    }
}

// Product.cs
using SmartData.Models;
using System.ComponentModel.DataAnnotations;

namespace MyApp
{
    public class Product
    {
        [Key]
        public int Id { get; set; }
        [TrackChange]
        public string Name { get; set; }
        [Embeddable("Product {Name} owned by customer {CustomerId}")]
        public string Description { get; set; }
        public int CustomerId { get; set; }
        public Customer Customer { get; set; }
    }
}
```

- `[Embeddable]`: Generates embeddings for semantic search and RAG-based Q&A.
- `[Timeseries]`: Stores timeseries data for IoT sensor readings.
- `[TrackChange]`: Logs changes to properties in `sysChangeLog`.
- `[EnsureIntegrity]`: Verifies data integrity with hash chains in `sysIntegrityLog`.

### 2. Set Up Dependency Injection

Configure `SmartData` with your database provider. Below are examples for SQLite (no migrations) and SQL Server (with migrations).

#### SQLite (No Migrations)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
making SmartData.Core;

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
services.AddSmartData<MyDbContext>(builder =>
{
    builder.WithConnectionString("Data Source=IoTData.db")
           .WithLogging(services.BuildServiceProvider().GetRequiredService<ILoggerFactory>())
           .WithEmbeddings()
           .WithTimeseries()
           .WithChangeTracking()
           .WithIntegrityVerification()
           .WithCalculations();
}, options => options.UseSqlite("Data Source=IoTData.db"));

var serviceProvider = services.BuildServiceProvider();
```

- **No Migrations**: Schema is created automatically, ideal for IoT prototyping.
- **Features**: Enables embeddings, timeseries, change tracking, integrity verification, and analytics.

#### SQL Server (With Migrations)

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartData.Core;

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
var loggerFactory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
services.AddSmartData<MyDbContext>(builder =>
{
    builder.WithConnectionString("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;")
           .WithMigrations("MyApp")
           .WithLogging(loggerFactory)
           .WithEmbeddings()
           .WithTimeseries()
           .WithChangeTracking()
           .WithIntegrityVerification()
           .WithCalculations();
}, options => options.UseSqlServer("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
    sqlOptions => sqlOptions.MigrationsAssembly("MyApp")));

var serviceProvider = services.BuildServiceProvider();
```

- **Migrations**: Recommended for production to manage schema changes.
- **SQL Server**: Supports robust IoT data storage and analytics.

### 3. Define Your DbContext

Create a context class to manage entities:

```csharp
using Microsoft.EntityFrameworkCore;
using SmartData.Data;

namespace MyApp
{
    public class MyDbContext : DataContext
    {
        public MyDbContext(DbContextOptions options, DataOptions dataOptions)
            : base(options, dataOptions) { }

        public DbSet<Sensor> Sensors { get; set; }
        public DbSet<Customer> Customers { get; set; }
        public DbSet<Product> Products { get; set; }

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

Manage IoT data, perform semantic searches, enable GPT-based Q&A, and run advanced analytics workflows:

```csharp
using Microsoft.Extensions.DependencyInjection;
using SmartData.Data;
using SmartData.Models;
using SmartData.AnalyticsService;

using var scope = serviceProvider.CreateScope();
var dbContext = scope.ServiceProvider.GetRequiredService<MyDbContext>();
var analyticsService = scope.ServiceProvider.GetRequiredService<SmartAnalyticsService>();

// Create schema (or apply migrations)
await dbContext.EnsureSchemaCreatedAsync();

// Insert IoT sensor data
var sensorService = new DataService<Sensor>(
    scope.ServiceProvider, 
    scope.ServiceProvider.GetRequiredService<DataOptions>(), 
    "Sensors",
    scope.ServiceProvider.GetService<IEmbeddingProvider>(),
    scope.ServiceProvider.GetService<IFaissSearch>(),
    scope.ServiceProvider.GetService<IEventBus>()
);
var sensor = new Sensor
{
    Id = "sensor1",
    Temperature = 70,
    Description = "Temperature is 70°F"
};
await sensorService.UpsertAsync(sensor);

// Insert relational data
var customerService = new DataService<Customer>(
    scope.ServiceProvider, 
    scope.ServiceProvider.GetRequiredService<DataOptions>(), 
    "Customers"
);
var productService = new DataService<Product>(
    scope.ServiceProvider, 
    scope.ServiceProvider.GetRequiredService<DataOptions>(), 
    "Products",
    scope.ServiceProvider.GetService<IEmbeddingProvider>(),
    scope.ServiceProvider.GetService<IFaissSearch>()
);
var customer = new Customer { Name = "John Doe", Email = "john.doe@example.com" };
await customerService.UpsertAsync(customer);
await productService.UpsertAsync(new Product
{
    Name = "Laptop",
    Description = "High-performance laptop",
    CustomerId = customer.Id
});

// Semantic search for RAG
var matches = await sensorService.SearchAsync("temperature 70", topK = 2);
foreach (var result in matches)
{
    Console.WriteLine($"Sensor Match: Id={result.GetValue<string>("Id")}, Temperature={result.GetValue<int>("Temperature")}°F, Score={result.GetValue<float>("Score")}");
}

// Timeseries data retrieval with interpolation
var timeseries = await sensorService.GetInterpolatedTimeseriesAsync(
    "sensor1", nameof(Sensor.Temperature), DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, TimeSpan.FromHours(1), InterpolationMethod.Linear);
Console.WriteLine("Temperature Trend (Last 7 Days):");
foreach (var data in timeseries)
{
    Console.WriteLine($"Timestamp: {data.Timestamp}, Temperature: {data.Value}°F");
}

// Define and run an analytics workflow
var analyticsConfig = new AnalyticsConfig
{
    Id = Guid.NewGuid(),
    Name = "TemperatureTrendAnalysis",
    Interval = 3600, // Run every hour
    Embeddable = true,
    Steps = new List<AnalyticsStepConfig>
    {
        new AnalyticsStepConfig
        {
            Type = AnalyticsStepType.SqlQuery,
            Config = "SELECT AVG(Temperature) as AvgTemp FROM Sensors WHERE Timestamp >= @p0",
            OutputVariable = "avgTemp",
            MaxLoop = 10
        },
        new AnalyticsStepConfig
        {
            Type = AnalyticsStepType.CSharp,
            Config = "context[\"avgTemp\"] > 75 ? \"High\" : \"Normal\"",
            OutputVariable = "tempStatus",
            MaxLoop = 10
        }
    }
};
await analyticsService.AddAnalyticsAsync(analyticsConfig);
var result = await analyticsService.ExecuteAnalyticsAsync(analyticsConfig.Id);
Console.WriteLine($"Analytics Result: {result}");

// Export analytics configuration
var exportedJson = await analyticsService.ExportAnalyticsAsync(analyticsConfig.Id);
Console.WriteLine($"Exported Analytics Config: {exportedJson}");
```

### Example Output

```
Tables in database: Sensors, Customers, Products, sysEmbeddings, sysTimeseriesBaseValues, sysTimeseriesDeltas, sysChangeLog, sysIntegrityLog, sysAnalytics, sysAnalyticsSteps
Inserted sensor sensor1 with temperature 70°F
Inserted customer John Doe (Id: 1)
Inserted product Laptop (Id: 1)
Sensor Match: Id=sensor1, Temperature=70°F, Score=1
Temperature Trend (Last 7 Days):
Timestamp: 2025-06-22T08:13:00Z, Temperature: 70°F
Analytics Result: Normal
Exported Analytics Config: {
  "Id": "guid-value",
  "Name": "TemperatureTrendAnalysis",
  "Interval": 3600,
  "Embeddable": true,
  "Steps": [
    {
      "Type": "SqlQuery",
      "Config": "SELECT AVG(Temperature) as AvgTemp FROM Sensors WHERE Timestamp >= @p0",
      "OutputVariable": "avgTemp",
      "MaxLoop": 10
    },
    {
      "Type": "CSharp",
      "Config": "context[\"avgTemp\"] > 75 ? \"High\" : \"Normal\"",
      "OutputVariable": "tempStatus",
      "MaxLoop": 10
    }
  ]
}
```

## Analytics Features

The `SmartAnalyticsService` enables advanced analytics workflows for IoT and smart building applications. Key capabilities include:

- **Step Types**:
  - `SqlQuery`: Execute SQL SELECT queries to aggregate data (e.g., AVG, SUM, COUNT).
  - `CSharp`: Run C# scripts for custom calculations or logic.
  - `Condition`: Implement conditional branching with boolean C# scripts.
  - `Variable`: Store intermediate results for use in subsequent steps.
  - `Timeseries`: Retrieve and analyze timeseries data with interpolation.
- **Triggers**:
  - **Time-Based**: Run analytics at specified intervals (e.g., every hour).
  - **Event-Driven**: Trigger analytics based on entity changes (insert, update, delete).
- **Change Tracking**: Automatically logs analytics results in `sysChangeLog` if `EnableChangeTracking` is enabled.
- **Validation**: Ensures safe SQL queries (only SELECT allowed) and C# scripts (no dangerous namespaces like `System.IO`).
- **Export/Import**: Serialize analytics configurations to JSON for portability.

### Example Analytics Workflow

Calculate the average temperature and determine if it's high or normal:

```csharp
var config = new AnalyticsConfig
{
    Id = Guid.NewGuid(),
    Name = "TemperatureCheck",
    Interval = -1, // Event-driven
    Steps = new List<AnalyticsStepConfig>
    {
        new AnalyticsStepConfig
        {
            Type = AnalyticsStepType.SqlQuery,
            Config = "SELECT AVG(Temperature) as AvgTemp FROM Sensors",
            OutputVariable = "avgTemp"
        },
        new AnalyticsStepConfig
        {
            Type = AnalyticsStepType.Condition,
            Config = "context[\"avgTemp\"] > 75",
            OutputVariable = "2", // Go to step 2 if true
            MaxLoop = 10
        },
        new AnalyticsStepConfig
        {
            Type = AnalyticsStepType.Variable,
            Config = "\"Normal\"",
            OutputVariable = "status"
        },
        new AnalyticsStepConfig
        {
            Type = AnalyticsStepType.Variable,
            Config = "\"High\"",
            OutputVariable = "status"
        }
    }
};
await analyticsService.AddAnalyticsAsync(config);
```

This workflow:
1. Calculates the average temperature using a SQL query.
2. Checks if the average is above 75°F.
3. Sets the status to "Normal" or "High" based on the condition.

## Configuration

- **Database Providers**: SQLite for IoT prototyping, SQL Server for production, or other EF Core providers.
- **Feature Flags**:
  - `WithEmbeddings()`: Enables vector embeddings for RAG Q&A and analytics.
  - `WithTimeseries()`: Enables timeseries data storage and retrieval.
  - `WithChangeTracking()`: Enables change logging for audit trails.
  - `WithIntegrityVerification()`: Enables data integrity checks with hash chains.
  - `WithCalculations()`: Enables advanced analytics via `SmartAnalyticsService`.
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

3. Configure `IDesignTimeDbContextFactory` for migrations:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using SmartData.Core;
using SmartData.Data;

namespace MyApp
{
    public class MyDbContextFactory : IDesignTimeDbContextFactory<MyDbContext>
    {
        public MyDbContext CreateDbContext(string[] args)
        {
            var services = new ServiceCollection();
            services.AddSmartData<MyDbContext>(builder =>
            {
                builder.WithConnectionString("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;")
                       .WithMigrations("MyApp")
                       .WithEmbeddings()
                       .WithTimeseries()
                       .WithChangeTracking()
                       .WithIntegrityVerification()
                       .WithCalculations();
            }, options => options.UseSqlServer("Server=localhost;Database=IoTData;Trusted_Connection=True;Persist Security Info=False;TrustServerCertificate=True;",
                sqlOptions => sqlOptions.MigrationsAssembly("MyApp")));

            var serviceProvider = services.BuildServiceProvider();
            return serviceProvider.GetRequiredService<MyDbContext>();
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