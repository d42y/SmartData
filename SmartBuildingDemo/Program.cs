using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartData.Core;
using SmartData.Data;
using SmartData.Models;
using SmartData.AnalyticsService;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

// Define entity models
public class Building
{
    public Guid Id { get; set; }
    [Required]
    public string Name { get; set; }
    [Embeddable("Building: {Name}", priority: 1)]
    public string Address { get; set; }
}

public class Sensor
{
    public Guid Id { get; set; }
    [Required]
    public Guid BuildingId { get; set; }
    [Required]
    public string Name { get; set; }
    [Timeseries]
    [TrackChange]
    public double Temperature { get; set; }
    [Timeseries]
    [TrackChange]
    public double Humidity { get; set; }
}

// Application-specific DbContext
public class AppDbContext : DataContext
{
    public AppDbContext(DbContextOptions options, DataOptions dataOptions, ILogger<DataContext> logger)
        : base(options, dataOptions, null, logger)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Building>().ToTable("Buildings");
        modelBuilder.Entity<Sensor>().ToTable("Sensors");
        modelBuilder.Entity<Sensor>()
            .HasOne<Building>()
            .WithMany()
            .HasForeignKey(s => s.BuildingId);
    }
}

// Context factory for migrations
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DataOptions()
            .WithConnectionString("Server=localhost;Database=SmartBuildingDemo;Trusted_Connection=True;TrustServerCertificate=True;")
            .WithMigrations()
            .WithTimeseries()
            .WithChangeTracking()
            .WithEmbeddings()
            .WithAnalytics();

        var dbContextOptionsBuilder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                options.ConnectionString,
                sqlOptions => sqlOptions.MigrationsAssembly(typeof(AppDbContextFactory).Assembly.GetName().Name));

        return new AppDbContext(dbContextOptionsBuilder.Options, options, null);
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        var host = CreateHostBuilder(args).Build();
        using (var scope = host.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var logger = services.GetRequiredService<ILogger<Program>>();

            try
            {
                // Ensure database is created and migrations are applied
                var dbContext = services.GetRequiredService<AppDbContext>();
                await dbContext.Database.MigrateAsync();
                logger.LogInformation("Database schema created and migrations applied.");

                // Register entities
                dbContext.RegisterEntity(typeof(Building), "Buildings");
                dbContext.RegisterEntity(typeof(Sensor), "Sensors");

                // Create a building
                var building = new Building
                {
                    Id = Guid.NewGuid(),
                    Name = "Tech Tower",
                    Address = "123 Innovation Drive"
                };
                await dbContext.AddAsync(building);
                await dbContext.SaveChangesAsync();
                logger.LogInformation("Inserted building: {BuildingName}", building.Name);

                // Create sensors with timeseries data
                var sensor1 = new Sensor
                {
                    Id = Guid.NewGuid(),
                    BuildingId = building.Id,
                    Name = "TemperatureSensor1",
                    Temperature = 22.5,
                    Humidity = 45.0
                };
                await dbContext.AddAsync(sensor1);
                await dbContext.SaveChangesAsync();
                logger.LogInformation("Inserted sensor: {SensorName}", sensor1.Name);

                // Update sensor data to demonstrate timeseries and change tracking
                sensor1.Temperature = 23.0;
                sensor1.Humidity = 46.0;
                await Task.Delay(1000); // Simulate time passing
                dbContext.Update(sensor1);
                await dbContext.SaveChangesAsync();
                logger.LogInformation("Updated sensor: {SensorName}", sensor1.Name);

                // Configure analytics for average temperature
                var analyticsService = services.GetRequiredService<SmartAnalyticsService>();
                var analyticsConfig = new AnalyticsConfig
                {
                    Id = Guid.NewGuid(),
                    Name = "AverageBuildingTemperature",
                    Interval = 60, // Run every 60 seconds
                    Embeddable = true,
                    Steps = new List<AnalyticsStepConfig>
                    {
                        new AnalyticsStepConfig
                        {
                            Type = AnalyticsStepType.Variable,
                            Config = $"return \"{building.Id}\";",
                            OutputVariable = "buildingId",
                            MaxLoop = 10
                        },
                        new AnalyticsStepConfig
                        {
                            Type = AnalyticsStepType.SqlQuery,
                            Config = "SELECT AVG(Temperature) as AvgTemp FROM Sensors WHERE BuildingId = {buildingId}",
                            OutputVariable = "AvgTemp",
                            MaxLoop = 10
                        }
                    }
                };
                await analyticsService.AddAnalyticsAsync(analyticsConfig);
                logger.LogInformation("Added analytics: {AnalyticsName}", analyticsConfig.Name);

                // Execute analytics
                var result = await analyticsService.ExecuteAnalyticsAsync(analyticsConfig.Id);
                logger.LogInformation("Analytics result for {AnalyticsName}: {Result}", analyticsConfig.Name, result);

                // Query timeseries data
                var sensorService = services.GetRequiredService<DataService<Sensor>>();
                var timeseries = await sensorService.GetTimeseriesAsync(
                    sensor1.Id.ToString(),
                    nameof(Sensor.Temperature),
                    DateTime.UtcNow.AddMinutes(-5),
                    DateTime.UtcNow
                );
                foreach (var ts in timeseries)
                {
                    logger.LogInformation("Timeseries - Timestamp: {Timestamp}, Temperature: {Value}", ts.Timestamp, ts.Value);
                }

                // Export analytics configuration
                var exportedJson = await analyticsService.ExportAnalyticsAsync(analyticsConfig.Id);
                logger.LogInformation("Exported analytics configuration: {Json}", exportedJson);

                // Import analytics (demonstrating import functionality)
                var newAnalyticsId = Guid.NewGuid();
                analyticsConfig.Id = newAnalyticsId;
                analyticsConfig.Name = "ImportedAverageTemperature";
                await analyticsService.ImportAnalyticsAsync(JsonSerializer.Serialize(analyticsConfig));
                logger.LogInformation("Imported analytics with new ID: {NewId}", newAnalyticsId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error occurred in the demo application.");
            }
        }

        await host.RunAsync();
    }

    static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging(builder =>
                {
                    builder.AddConsole();
                    builder.SetMinimumLevel(LogLevel.Information);
                });

                services.AddSmartData<AppDbContext>(options =>
                {
                    options.WithConnectionString("Server=localhost;Database=SmartBuildingDemo;Trusted_Connection=True;TrustServerCertificate=True;")
                           .WithMigrations()
                           .WithLogging(services.BuildServiceProvider().GetRequiredService<ILoggerFactory>())
                           .WithTimeseries()
                           .WithChangeTracking()
                           .WithEmbeddings()
                           .WithCalculations();
                }, dbOptions =>
                {
                    dbOptions.UseSqlServer(
                        "Server=localhost;Database=SmartBuildingDemo;Trusted_Connection=True;TrustServerCertificate=True;",
                        sqlOptions => sqlOptions.MigrationsAssembly(typeof(AppDbContextFactory).Assembly.GetName().Name));
                });
            });
}